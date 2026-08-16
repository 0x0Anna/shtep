using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using System.IO;
using TelemetryExportPlugin.Boundaries;
using TelemetryExportPlugin.Config;
using TelemetryExportPlugin.Export;
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

        // Sims confirmed to have a real player-facing rewind feature (see
        // DiscontinuityDetector.Evaluate's rewindCapable doc comment for why this
        // gates Backward-vs-Forward classification). "FH6" confirmed via this
        // repo's own fixtures/live testing (fixtures/rewind/fh6_freeroam_*).
        // Anna confirmed 2026-08-04 that GranTurismo7 has no such feature at all
        // (only lap/session restart) - don't add it here. Default for any sim not
        // listed is "not rewind capable" (the safer default), not "unconfirmed."
        private static readonly HashSet<string> RewindCapableSimIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FH6",
        };

        private static readonly Regex NonSafeChars = new Regex("[^A-Za-z0-9_-]");

        // StatusDataBase.ReplayMode is a plain System.String property (confirmed by
        // reflecting the installed GameReaderCommon.dll) that tracks SimHub's own
        // record toggle every tick - live logs only ever showed exactly these two
        // values. Unlike the DataCorePlugin.LoggingLastMessage approach originally
        // tried here, this is a persistent per-tick property, not a transient log
        // line that gets overwritten by the next unrelated message - no edge-trigger
        // latch needed, just compare current vs previous tick. Confirmed live
        // 2026-08-02 against SimHub's own record toggle during a real ACR session.
        private const string SimHubReplayModeRecording = "Record";

        // Reuses ChannelMap's own (try/catch-guarded) LapNumber accessor rather
        // than duplicating the "not every sim exposes this" handling here.
        private static readonly Func<StatusDataBase, string, double?> GetLapNumber =
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
        private DisconnectGuard _disconnectGuard;
        private RecordingSession _session;
        private string _pluginVersion;

        private string _currentSim;
        private int _plausibleStreak;
        private double? _lastLapNumber;
        private DateTime _lastDiagLogUtc;
        private double? _openDiscontinuityStartTimeS;
        private List<DiscontinuityEntry> _discontinuities;
        private List<RewindEntry> _rewinds;
        private bool _simHubRecordingActive;

        public void Init(PluginManager pluginManager)
        {
            SimHub.Logging.Current.Info("TelemetryExportPlugin: starting");

            Settings = this.ReadCommonSettings<PluginSettings>("GeneralSettings", () => new PluginSettings());
            _pluginVersion = GetType().Assembly.GetName().Version.ToString();

            // Defensive, not trusting SimHub's ReadCommonSettings to honor
            // [JsonProperty(ObjectCreationHandling.Replace)] on EnabledChannels - it
            // doesn't, in practice (see PluginSettings.cs's comment on that property).
            // This runs every Init() regardless, so it also self-heals a settings file
            // already corrupted by a prior version of this plugin.
            Settings.EnabledChannels = Settings.EnabledChannels.Distinct().ToList();

            ValidateConfiguredPaths();
            CrashRecovery.RecoverAll(Settings.TempDir, Settings.OutputDir, Settings, _pluginVersion, msg => SimHub.Logging.Current.Error(msg));

            _discontinuityDetector = new DiscontinuityDetector(Settings);
            _rewindIndex = new RewindIndex();
            _disconnectGuard = new DisconnectGuard(Settings.DisconnectGraceMs);

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
            if (data.NewData == null)
            {
                // All of CircuitBoundary/RallyBoundary/EvaluateSimHubRecordingTrigger
                // only ever run below this guard, so none of them can react to
                // anything once the game disconnects - confirmed live: SimHub keeps
                // calling DataUpdate for tens of seconds after "Game disconnected"
                // (other plugins/subsystems stay active), but data.NewData stays null
                // the whole time, so a session left open at disconnect would otherwise
                // never close until SimHub itself shuts down and Plugin.End() runs.
                // Stop SampleTimer's cadence for the duration of the gap, same as a
                // real in-game pause - otherwise it keeps ticking on held last-known
                // values the whole time data.NewData is null, and with
                // DisconnectGraceMs raised well past a single tick (see
                // PluginSettings.cs), an unconfirmed disconnect would otherwise
                // inject a run of frozen stale-value rows into the file instead of
                // just a clean pause-style gap.
                _sampleTimer.SetPaused(true);

                // Debounced via DisconnectGuard rather than ending on the very first
                // null tick - confirmed live 2026-08-07 that GT7's telemetry goes null
                // for well under a second around an in-game pause, and the old
                // immediate-end behavior fragmented one continuous drive into several
                // files every time (see DisconnectGuard's doc comment).
                if (_disconnectGuard.Feed(hasData: false, DateTime.UtcNow) && _session != null && _session.IsOpen)
                {
                    SimHub.Logging.Current.Info("TelemetryExportPlugin: game disconnected mid-session, ending recording");
                    EndSession();
                }
                return;
            }

            _disconnectGuard.Feed(hasData: true, DateTime.UtcNow);
            _currentSim = data.GameName;
            _sampleTimer.SetPaused(data.GamePaused);

            var d = data.NewData;
            foreach (var (header, getValue) in ChannelMap.Definitions)
            {
                if (!Settings.EnabledChannels.Contains(header)) continue;
                var value = getValue(d, _currentSim);
                if (value.HasValue)
                {
                    _sampleTimer.UpdateChannel(header, value.Value);
                }
            }

            LogDiagnosticsIfEnabled(data, d);

            // LapDistance_m doubles as the position signal for discontinuity/rewind
            // detection - PosX/Y/Z aren't available generically (see ChannelMap.cs).
            double position = d.TrackPositionMeters;

            double? lapNumber = GetLapNumber(d, _currentSim);
            bool lapJustChanged = lapNumber.HasValue && _lastLapNumber.HasValue && lapNumber.Value != _lastLapNumber.Value;
            _lastLapNumber = lapNumber;

            if (_session != null && _session.IsOpen)
            {
                EvaluateDiscontinuityAndRewind(position, lapJustChanged);
            }

            if (Settings.RecordingTrigger == RecordingTriggerMode.SimHubRecording)
            {
                EvaluateSimHubRecordingTrigger(d);
            }
            else if (RallySimIds.Contains(_currentSim ?? string.Empty))
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

        // See RecordingTriggerMode's doc comment and SimHubReplayModeRecording's
        // comment above - starts/ends sessions off SimHub's own record toggle
        // (d.ReplayMode) instead of CircuitBoundary/RallyBoundary's pit-lane
        // heuristic.
        private void EvaluateSimHubRecordingTrigger(StatusDataBase d)
        {
            bool recording = d.ReplayMode == SimHubReplayModeRecording;

            if (!_simHubRecordingActive && recording)
            {
                _simHubRecordingActive = true;
                StartSession("stint", d.TrackName, d.CarModel, d.PlayerName);
            }
            else if (_simHubRecordingActive && !recording)
            {
                _simHubRecordingActive = false;
                EndSession();
            }
        }

        private void EvaluateDiscontinuityAndRewind(double position, bool lapJustChanged)
        {
            // Time_s isn't tracked on RecordingSession itself; the rewind index's
            // row count is already kept in lockstep with rows written, so reuse it
            // rather than duplicating a counter here.
            double timeS = _rewindIndex.Count / (double)Settings.SampleRateHz;

            bool rewindCapable = RewindCapableSimIds.Contains(_currentSim ?? string.Empty);
            var kind = _discontinuityDetector.Evaluate(position, timeS, simReportsResetOrAssist: false, lapJustChanged: lapJustChanged, rewindCapable: rewindCapable);

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

            // Centralized here (not just EvaluateSimHubRecordingTrigger's own path) so
            // every way a session can end - pit-lane, disconnect, plugin shutdown -
            // leaves the latch consistent with "no session open" for whenever the game
            // next reconnects, regardless of which trigger mode originally opened it.
            _simHubRecordingActive = false;

            // Same reasoning for CircuitBoundary's own internal latch - without this,
            // a session force-closed by something other than a pit-lane transition
            // (disconnect being the common case) leaves _inStint stuck true, and the
            // next Feed() with an unchanged rawInPitLane value silently never starts
            // a new stint. See CircuitBoundary.Reset()'s doc comment.
            _circuitBoundary.Reset();

            // Discard stub sessions rather than writing them out - see
            // PluginSettings.MinSessionDurationS's doc comment. Uses
            // _rewindIndex.Count (already kept in lockstep with rows actually
            // written, same as EvaluateDiscontinuityAndRewind's own timeS calc)
            // rather than wall-clock session length, so a session that spent most
            // of its short life paused/disconnected doesn't get held to the same
            // bar as one that was genuinely driven for that long.
            double recordedDurationS = _rewindIndex.Count / (double)Settings.SampleRateHz;
            if (recordedDurationS < Settings.MinSessionDurationS)
            {
                SimHub.Logging.Current.Info(
                    $"TelemetryExportPlugin: discarding session, only {recordedDurationS:0.000}s recorded (below MinSessionDurationS={Settings.MinSessionDurationS}s)");
                _session.Discard();
                _session = null;
                _openDiscontinuityStartTimeS = null;
                return;
            }

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

            if (Settings.ExportMotecLd)
            {
                ExportMotecLdIfEnabled(_session.BaseName, sidecar);
            }

            if (Settings.ExportIbt)
            {
                ExportIbtIfEnabled(_session.BaseName, sidecar);
            }

            _session = null;
        }

        // Runs strictly after RecordingSession.Close() has moved the .tsv/.meta.json
        // pair into OutputDir - reads the finished pair back rather than hooking
        // into the live write path. Failure here must never take down recording,
        // so it's caught and logged rather than propagated.
        private void ExportMotecLdIfEnabled(string baseName, RecordingSidecar sidecar)
        {
            try
            {
                string tsvPath = Path.Combine(Settings.OutputDir, $"{baseName}.tsv");
                string motecOutputDir = string.IsNullOrWhiteSpace(Settings.MotecOutputDir)
                    ? Settings.OutputDir
                    : Settings.MotecOutputDir;

                string ldPath = MotecExporter.Export(tsvPath, sidecar, motecOutputDir, baseName);
                SimHub.Logging.Current.Info($"TelemetryExportPlugin: wrote MoTeC log {ldPath}");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"TelemetryExportPlugin: MoTeC export failed for {baseName}: {ex}");
            }
        }

        // Same contract as ExportMotecLdIfEnabled: runs after the .tsv/.meta.json
        // pair has landed in OutputDir, and a failure here is logged rather than
        // propagated so it can never take down recording.
        private void ExportIbtIfEnabled(string baseName, RecordingSidecar sidecar)
        {
            try
            {
                string tsvPath = Path.Combine(Settings.OutputDir, $"{baseName}.tsv");
                string ibtOutputDir = string.IsNullOrWhiteSpace(Settings.IbtOutputDir)
                    ? Settings.OutputDir
                    : Settings.IbtOutputDir;

                string ibtPath = IbtExporter.Export(tsvPath, sidecar, ibtOutputDir, baseName,
                    Settings.IbtTickRateHz);
                SimHub.Logging.Current.Info($"TelemetryExportPlugin: wrote iRacing log {ibtPath}");
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Error($"TelemetryExportPlugin: .ibt export failed for {baseName}: {ex}");
            }
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
            var fields = string.Join(", ", ChannelMap.Definitions.Select(def => $"{def.Header}={FormatDiag(def.GetValue(d, _currentSim))}"));
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
