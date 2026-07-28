using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using TelemetryExportPlugin.Boundaries;
using TelemetryExportPlugin.Config;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin
{
    [PluginDescription("Records telemetry to TSV + JSON sidecar pairs for shakedown-engineer")]
    [PluginAuthor("Codify Systems")]
    [PluginName("Telemetry Export")]
    public class Plugin : IPlugin, IDataPlugin, IWPFSettingsV2
    {
        // Reflected against SimHub 9.x's installed GameReaderCommon.dll; these are
        // SimHub's own short game ids from data.GameName, not guesses. Extend as
        // you test against more sims (PLUGIN_IMPLEMENTATION_PLAN.md step 9).
        private static readonly HashSet<string> RallySimIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "RichardBurnsRally", "DirtRally2_0", "Ea Sports Wrc",
        };

        private static readonly Regex NonSafeChars = new Regex("[^A-Za-z0-9_-]");

        // Reuses ChannelMap's own (try/catch-guarded) LapNumber accessor rather
        // than duplicating the "not every sim exposes this" handling here.
        private static readonly Func<StatusDataBase, double?> GetLapNumber =
            ChannelMap.Definitions.First(def => def.Header == "LapNumber").GetValue;

        public PluginSettings Settings;

        public PluginManager PluginManager { get; set; }

        public ImageSource PictureIcon => null;

        public string LeftMenuTitle => "Telemetry Export";

        private SampleTimer _sampleTimer;
        private DiscontinuityDetector _discontinuityDetector;
        private RewindIndex _rewindIndex;
        private RallyBoundary _rallyBoundary;
        private CircuitBoundary _circuitBoundary;
        private RecordingSession _session;
        private string _pluginVersion;

        private string _currentSim;
        private int _plausibleStreak;
        private double? _lastLapNumber;
        private DateTime _lastDiagLogUtc;
        private double? _openDiscontinuityStartTimeS;
        private List<DiscontinuityEntry> _discontinuities;
        private List<RewindEntry> _rewinds;

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("TelemetryExportPlugin: starting");

            Settings = this.ReadCommonSettings<PluginSettings>("GeneralSettings", () => new PluginSettings());
            _pluginVersion = GetType().Assembly.GetName().Version.ToString();

            ValidateConfiguredPaths();
            CrashRecovery.RecoverAll(Settings.TempDir, Settings.OutputDir, Settings, _pluginVersion, msg => SimHub.Logging.Current.Error(msg));

            _discontinuityDetector = new DiscontinuityDetector(Settings);
            _rewindIndex = new RewindIndex();

            _rallyBoundary = new RallyBoundary();
            _rallyBoundary.StageStarted += (context, car, driver) =>
            {
                SimHub.Logging.Current.Info($"TelemetryExportPlugin: RallyBoundary.StageStarted context={context} car={car} driver={driver}");
                StartSession("stage", context, car, driver);
            };
            _rallyBoundary.StageEnded += () =>
            {
                SimHub.Logging.Current.Info("TelemetryExportPlugin: RallyBoundary.StageEnded");
                EndSession();
            };

            _circuitBoundary = new CircuitBoundary(Settings.PitLaneDebounceMs);
            _circuitBoundary.StintStarted += (context, car, driver) =>
            {
                SimHub.Logging.Current.Info($"TelemetryExportPlugin: CircuitBoundary.StintStarted context={context} car={car} driver={driver}");
                StartSession("stint", context, car, driver);
            };
            _circuitBoundary.StintEnded += () =>
            {
                SimHub.Logging.Current.Info("TelemetryExportPlugin: CircuitBoundary.StintEnded");
                EndSession();
            };

            _sampleTimer = new SampleTimer(Settings.SampleRateHz);
            _sampleTimer.RowReady += OnRowReady;
            _sampleTimer.Start();
        }

        private void ValidateConfiguredPaths()
        {
            var tempResult = PathValidation.ValidateWritableDirectory(Settings.TempDir, nameof(Settings.TempDir));
            if (!tempResult.Success)
            {
                SimHub.Logging.Current.Warn($"TelemetryExportPlugin: {tempResult.Message}");
            }

            var outputResult = PathValidation.ValidateWritableDirectory(Settings.OutputDir, nameof(Settings.OutputDir));
            if (!outputResult.Success)
            {
                SimHub.Logging.Current.Warn($"TelemetryExportPlugin: {outputResult.Message}");
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (data.NewData == null) return;

            _currentSim = data.GameName;
            _sampleTimer.SetPaused(data.GamePaused);

            var d = data.NewData;
            foreach (var (header, getValue) in ChannelMap.Definitions)
            {
                if (!Settings.EnabledChannels.Contains(header)) continue;
                var value = getValue(d);
                if (value.HasValue)
                {
                    _sampleTimer.UpdateChannel(header, value.Value);
                }
            }

            LogDiagnosticsIfEnabled(data, d);

            // LapDistance_m doubles as the position signal for discontinuity/rewind
            // detection - PosX/Y/Z aren't available generically (see ChannelMap.cs).
            double position = d.TrackPositionMeters;

            double? lapNumber = GetLapNumber(d);
            bool lapJustChanged = lapNumber.HasValue && _lastLapNumber.HasValue && lapNumber.Value != _lastLapNumber.Value;
            _lastLapNumber = lapNumber;

            if (_session != null && _session.IsOpen)
            {
                EvaluateDiscontinuityAndRewind(position, lapJustChanged);
            }

            if (RallySimIds.Contains(_currentSim ?? string.Empty))
            {
                // TODO: no confirmed stage-start/stage-end signal yet for any rally
                // adapter - needs a live rally sim session to identify (see
                // RallyBoundary.cs and PLUGIN_IMPLEMENTATION_PLAN.md step 3).
                // RallyBoundary is intentionally left un-driven until then.
            }
            else
            {
                bool rawInPitLane = d.IsInPitLane != 0;
                _circuitBoundary.Feed(rawInPitLane, DateTime.UtcNow, d.TrackName, d.CarModel, d.PlayerName);
            }
        }

        private void EvaluateDiscontinuityAndRewind(double position, bool lapJustChanged)
        {
            // Time_s isn't tracked on RecordingSession itself; the rewind index's
            // row count is already kept in lockstep with rows written, so reuse it
            // rather than duplicating a counter here.
            double timeS = _rewindIndex.Count / (double)Settings.SampleRateHz;

            var kind = _discontinuityDetector.Evaluate(position, timeS, simReportsResetOrAssist: false, lapJustChanged: lapJustChanged);

            switch (kind)
            {
                case DiscontinuityKind.Forward:
                    SimHub.Logging.Current.Info($"TelemetryExportPlugin: forward discontinuity detected at t={timeS:0.000}s position={position:0.0}m");
                    if (!_openDiscontinuityStartTimeS.HasValue)
                    {
                        _openDiscontinuityStartTimeS = timeS;
                    }
                    _sampleTimer.SetDiscontinuity(true);
                    _plausibleStreak = 0;
                    break;

                case DiscontinuityKind.Backward:
                    if (Settings.RewindHandling == RewindHandlingMode.FlagOnly)
                    {
                        // SCHEMA.md "RewindHandling": FlagOnly keeps every row and
                        // treats the boundary like a Discontinuity instead of
                        // truncating - same open/close-window tracking as Forward,
                        // just entered from the Backward direction.
                        SimHub.Logging.Current.Info($"TelemetryExportPlugin: rewind detected (FlagOnly, not truncating) at t={timeS:0.000}s position={position:0.0}m");
                        if (!_openDiscontinuityStartTimeS.HasValue)
                        {
                            _openDiscontinuityStartTimeS = timeS;
                        }
                        _sampleTimer.SetDiscontinuity(true);
                        _plausibleStreak = 0;
                        break;
                    }

                    if (_rewindIndex.TryFindTruncationPoint(position, out var entry))
                    {
                        // entry.RowIndex is the 0-based index of the row we're
                        // truncating TO (it survives - TrimAfter keeps it). The
                        // next row written must therefore resume at RowIndex + 1,
                        // not RowIndex itself - passing RowIndex here would make
                        // the next emitted row duplicate the surviving row's own
                        // Time_s (breaking "monotonic from 0.000" per SCHEMA.md)
                        // and would over-count rowsRemoved by one.
                        int rowsRemoved = (int)(_rewindIndex.Count - (entry.RowIndex + 1));
                        _session.TruncateTo(entry.ByteOffset);
                        _rewindIndex.TrimAfter(entry.RowIndex);
                        _sampleTimer.ResetRowIndexTo(entry.RowIndex + 1);
                        _rewinds.Add(new RewindEntry
                        {
                            TruncatedFromTimeS = timeS,
                            TruncatedToTimeS = entry.TimeS,
                            RowsRemoved = rowsRemoved,
                        });
                        // Any discontinuity window still open at this point covered
                        // rows that just got truncated away - moot, discard it.
                        _openDiscontinuityStartTimeS = null;
                        SimHub.Logging.Current.Info(
                            $"TelemetryExportPlugin: rewind detected, truncated {rowsRemoved} rows back to t={entry.TimeS:0.000}s");
                    }
                    break;

                case DiscontinuityKind.None:
                    // Clear the flag once position has advanced plausibly for a few
                    // consecutive ticks - a rough proxy for "the assist/reset window
                    // ended", since no generic sim-exposed end-of-window signal
                    // exists to key off instead. Revisit once a real rally sim
                    // exposes one (PLUGIN_IMPLEMENTATION_PLAN.md step 5).
                    _plausibleStreak++;
                    if (_plausibleStreak > 3)
                    {
                        _sampleTimer.SetDiscontinuity(false);
                        if (_openDiscontinuityStartTimeS.HasValue)
                        {
                            // "unknown" per SCHEMA.md - this is heuristic-only
                            // detection (simReportsResetOrAssist is always false
                            // above; no sim-exposed reason available to report).
                            _discontinuities.Add(new DiscontinuityEntry
                            {
                                StartTimeS = _openDiscontinuityStartTimeS.Value,
                                EndTimeS = timeS,
                                Reason = "unknown",
                            });
                            _openDiscontinuityStartTimeS = null;
                        }
                    }
                    break;
            }
        }

        private void OnRowReady(double timeS, IReadOnlyDictionary<string, double> values)
        {
            if (_session == null || !_session.IsOpen) return;

            _session.WriteRow(timeS, values);
            _rewindIndex.Add(values.TryGetValue("LapDistance_m", out var pos) ? pos : 0, _session.CurrentByteOffset, _rewindIndex.Count, timeS);
        }

        private void StartSession(string sessionType, string context, string car, string driver)
        {
            if (_session != null && _session.IsOpen)
            {
                EndSession();
            }

            string sim = SanitizeToken(_currentSim ?? "unknown");
            string safeContext = SanitizeToken(context ?? "unknown");
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string baseName = $"{sim}_{safeContext}_{timestamp}";

            _session = new RecordingSession(Settings.TempDir, Settings.OutputDir, baseName, Settings.EnabledChannels);
            _session.Open();
            _sampleTimer.ResetForNewSession();
            _rewindIndex.Clear();
            _discontinuityDetector.Reset();
            _plausibleStreak = 0;
            _lastLapNumber = null;
            _openDiscontinuityStartTimeS = null;
            _discontinuities = new List<DiscontinuityEntry>();
            _rewinds = new List<RewindEntry>();

            _sessionStartUtc = DateTime.UtcNow;
            _sessionSessionType = sessionType;
            _sessionContext = context;
            _sessionCar = car;
            _sessionDriver = driver;
        }

        private DateTime _sessionStartUtc;
        private string _sessionSessionType;
        private string _sessionContext;
        private string _sessionCar;
        private string _sessionDriver;

        private void EndSession()
        {
            if (_session == null || !_session.IsOpen) return;

            if (_openDiscontinuityStartTimeS.HasValue)
            {
                double timeS = _rewindIndex.Count / (double)Settings.SampleRateHz;
                _discontinuities.Add(new DiscontinuityEntry
                {
                    StartTimeS = _openDiscontinuityStartTimeS.Value,
                    EndTimeS = timeS,
                    Reason = "unknown",
                });
                _openDiscontinuityStartTimeS = null;
            }

            var sidecar = new RecordingSidecar
            {
                Sim = _currentSim,
                SessionType = _sessionSessionType,
                Context = _sessionContext,
                Car = _sessionCar,
                Driver = _sessionDriver,
                StartTimeUtc = _sessionStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                EndTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                SampleRateHz = Settings.SampleRateHz,
                // "Paused"/"Discontinuity" are always present in the .tsv
                // regardless of EnabledChannels (see RecordingSession.FixedColumns) -
                // the sidecar's channel list must reflect every column actually in
                // the file, per SCHEMA.md ("exact list/order of data columns
                // actually present in the paired .tsv").
                Channels = new List<string> { "Paused", "Discontinuity" }.Concat(Settings.EnabledChannels).ToList(),
                Discontinuities = _discontinuities.Count > 0 ? _discontinuities : null,
                Rewinds = _rewinds.Count > 0 ? _rewinds : null,
                PluginVersion = _pluginVersion,
            };

            _session.Close(sidecar);
            _session = null;
        }

        // Throttled raw-channel dump to SimHub's log (not the recorded TSV) - lets
        // us verify ChannelMap accessors against a new sim's real telemetry
        // without attaching a debugger. Gated behind VerboseDiagnosticLogging
        // (off by default) so a published build doesn't spam every user's log;
        // flip it on in settings when bringing up a new sim adapter.
        private void LogDiagnosticsIfEnabled(GameData data, StatusDataBase d)
        {
            if (!Settings.VerboseDiagnosticLogging) return;
            if ((DateTime.UtcNow - _lastDiagLogUtc).TotalSeconds < 5) return;

            _lastDiagLogUtc = DateTime.UtcNow;
            var fields = string.Join(", ", ChannelMap.Definitions.Select(def => $"{def.Header}={FormatDiag(def.GetValue(d))}"));
            SimHub.Logging.Current.Info(
                $"TelemetryExportPlugin: diag sim={_currentSim} track={d.TrackName} car={d.CarModel} " +
                $"gearRaw={d.Gear} isInPitLane={d.IsInPitLane} paused={data.GamePaused} " +
                $"sessionType={d.SessionTypeName} isSessionRestart={d.IsSessionRestart} " +
                $"completedLaps={d.CompletedLaps} totalLaps={d.TotalLaps} remainingLaps={d.RemainingLaps} " +
                $"isGameReplay={d.IsGameReplay} replayMode={d.ReplayMode} isLapValid={d.IsLapValid} " +
                $"flagCheckered={d.Flag_Checkered} flagGreen={d.Flag_Green} sessionTimeLeft={d.SessionTimeLeft} " +
                $"{fields}");
        }

        private static string FormatDiag(double? value)
        {
            return value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "null";
        }

        private static string SanitizeToken(string value)
        {
            return NonSafeChars.Replace(value.Replace(' ', '_'), "");
        }

        public void End(PluginManager pluginManager)
        {
            EndSession();
            _sampleTimer?.Stop();
            _sampleTimer?.Dispose();
            this.SaveCommonSettings("GeneralSettings", Settings);
        }

        Control _settingsControl;
        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return _settingsControl = new Control(this);
        }
    }
}
