using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Export
{
    /// <summary>
    /// Optional post-processor: converts a completed {base}.tsv + {base}.meta.json
    /// pair (per SCHEMA.md) into an iRacing {base}.ibt file, which Cosworth Pi
    /// Toolbox imports natively (Import -> iRacing).
    ///
    /// Runs strictly after RecordingSession.Close() has moved both files into
    /// OutputDir - reads the finished pair back rather than hooking into the live
    /// write path, so the hot recording path has no knowledge this exists. Same
    /// shape and constraints as MotecExporter.
    ///
    /// See PI_TOOLBOX_EXPORT.md for the research record behind the channel set,
    /// the YAML contract, and the unit conversions.
    /// </summary>
    public static class IbtExporter
    {
        /// <summary>
        /// iRacing's own tick rate. Pi Toolbox was validated against 60 Hz files;
        /// shtep records at 100 Hz by default, so samples are resampled onto a
        /// uniform 60 Hz grid.
        ///
        /// This is the trap that produces a file passing every structural check
        /// while still being wrong: consumers reconstruct the time axis as
        /// index * (1/tick_rate), so declaring 60 while writing 100 Hz data
        /// stretches the session by 1.667x with every value round-tripping
        /// correctly. tick_rate and the actual sample spacing must agree.
        /// </summary>
        public const int DefaultTickRateHz = 60;

        public static string Export(string tsvPath, RecordingSidecar sidecar, string outputDir,
            string baseName, int tickRateHz = DefaultTickRateHz)
        {
            if (tickRateHz <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tickRateHz), "Tick rate must be positive");
            }

            var samples = ReadTsv(tsvPath);
            if (samples.RowCount < 2)
            {
                throw new InvalidOperationException(
                    $"TSV has too few rows to export ({samples.RowCount}): {tsvPath}");
            }

            var laps = LapAnalysis.From(samples);
            var grid = Resampler.Build(samples, laps, tickRateHz);

            var session = new IbtSessionInfo
            {
                TrackName = string.IsNullOrEmpty(sidecar.Context) ? "Unknown" : sidecar.Context,
                TrackConfigName = sidecar.SessionType == "stage" ? "Stage" : "Circuit",
                CarName = string.IsNullOrEmpty(sidecar.Car) ? "Unknown" : sidecar.Car,
                DriverName = string.IsNullOrEmpty(sidecar.Driver) ? "Me" : sidecar.Driver,
                TrackLengthM = laps.TrackLengthM,
                EstLapTimeS = laps.EstLapTimeS,
                LapCount = laps.LapCount,
                GearCountForward = grid.MaxGear > 0 ? grid.MaxGear : 6,
                StartTimeUtc = ParseTimestamp(sidecar.StartTimeUtc),
                DurationS = grid.DurationS,
            };

            Directory.CreateDirectory(outputDir);
            string ibtPath = Path.Combine(outputDir, $"{baseName}.ibt");
            IbtWriter.Write(ibtPath, session, grid.Channels, tickRateHz);
            return ibtPath;
        }

        private static DateTime ParseTimestamp(string isoUtc)
        {
            DateTime parsed;
            if (!string.IsNullOrEmpty(isoUtc) &&
                DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed;
            }
            return DateTime.UtcNow;
        }

        // ------------------------------------------------------------------
        // TSV reading
        // ------------------------------------------------------------------

        internal sealed class SampleTable
        {
            public List<double> TimeS = new List<double>();
            public Dictionary<string, List<double>> Columns =
                new Dictionary<string, List<double>>(StringComparer.Ordinal);

            public int RowCount { get { return TimeS.Count; } }

            public List<double> Column(string name)
            {
                List<double> values;
                return Columns.TryGetValue(name, out values) ? values : null;
            }

            /// <summary>True when the column exists and holds more than one distinct value.</summary>
            public bool IsLive(string name)
            {
                var values = Column(name);
                if (values == null || values.Count == 0) return false;
                double first = values[0];
                for (int i = 1; i < values.Count; i++)
                {
                    if (values[i] != first) return true;
                }
                return false;
            }
        }

        internal static SampleTable ReadTsv(string tsvPath)
        {
            var lines = File.ReadAllLines(tsvPath, new UTF8Encoding(false));
            if (lines.Length == 0)
            {
                throw new InvalidOperationException($"TSV file is empty: {tsvPath}");
            }

            var header = lines[0].Split('\t');

            // Recorded files can carry the same channel repeated many times (the
            // SimHub ReadCommonSettings list-append bug duplicates EnabledChannels
            // on every plugin load). Every repeat holds identical values, so the
            // first occurrence of each name wins. Reading by last occurrence - what
            // a naive dictionary build does - is equally valid data but needlessly
            // surprising; first is what the analysis tooling assumes.
            var columnIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int c = 0; c < header.Length; c++)
            {
                if (!columnIndex.ContainsKey(header[c])) columnIndex[header[c]] = c;
            }

            int timeCol;
            if (!columnIndex.TryGetValue("Time_s", out timeCol))
            {
                throw new InvalidOperationException($"TSV has no Time_s column: {tsvPath}");
            }

            var table = new SampleTable();
            var names = columnIndex.Keys.Where(n => n != "Time_s").ToList();
            foreach (var name in names) table.Columns[name] = new List<double>();
            var lastKnown = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var name in names) lastKnown[name] = 0.0;

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var cells = lines[i].Split('\t');
                if (cells.Length <= timeCol) continue;

                double t;
                if (!double.TryParse(cells[timeCol], NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                {
                    continue;
                }
                table.TimeS.Add(t);

                foreach (var name in names)
                {
                    int c = columnIndex[name];
                    // An empty cell means "no value this row" (RecordingSession.WriteRow) -
                    // hold the last known value rather than throwing away the export
                    // over one gap in one channel, matching MotecExporter.
                    double value;
                    string cell = c < cells.Length ? cells[c] : "";
                    if (string.IsNullOrEmpty(cell) ||
                        !double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    {
                        value = lastKnown[name];
                    }
                    lastKnown[name] = value;
                    table.Columns[name].Add(value);
                }
            }

            return table;
        }

        // ------------------------------------------------------------------
        // Lap / distance analysis
        // ------------------------------------------------------------------

        /// <summary>
        /// Derives lap structure and a distance axis.
        ///
        /// Distance is integrated from speed rather than taken from LapDistance_m,
        /// because that channel is unusable across every sim tested: flat zero for
        /// the rally sims, and for GT7 a near-constant value with rare jumps (one
        /// increasing step across 8000 rows). LapDistancePct is likewise a flat -1
        /// sentinel there. Integration was cross-validated on a 7-lap GT7 session:
        /// five full laps agreed to 0.34%.
        ///
        /// For a circuit stint the accumulator resets at each LapNumber change, so
        /// LapDist means "metres travelled from S/F this lap" as iRacing defines it.
        /// For a rally stage it accumulates across the whole run, which is the
        /// meaningful axis there.
        /// </summary>
        internal sealed class LapAnalysis
        {
            public List<double> LapDist = new List<double>();
            public List<double> LapNumber = new List<double>();
            public double TrackLengthM;
            public double EstLapTimeS;
            public int LapCount;
            public bool IsCircuit;

            public static LapAnalysis From(SampleTable t)
            {
                var result = new LapAnalysis();
                int n = t.RowCount;
                var speed = t.Column("Speed_kmh");
                var lapCol = t.Column("LapNumber");

                result.IsCircuit = t.IsLive("LapNumber");

                result.LapDist = new List<double>(n);
                result.LapNumber = new List<double>(n);

                var lapLengths = new List<double>();
                var lapEndTimes = new List<double>();

                double distance = 0.0;
                for (int i = 0; i < n; i++)
                {
                    if (i > 0)
                    {
                        bool boundary = result.IsCircuit && lapCol[i] != lapCol[i - 1];
                        if (boundary)
                        {
                            lapLengths.Add(distance);
                            lapEndTimes.Add(t.TimeS[i - 1]);
                            distance = 0.0;
                        }
                        else if (speed != null)
                        {
                            double dt = t.TimeS[i] - t.TimeS[i - 1];
                            if (dt > 0) distance += (speed[i] / 3.6) * dt;
                        }
                    }
                    result.LapDist.Add(distance);
                    result.LapNumber.Add(lapCol != null ? lapCol[i] : 0.0);
                }
                lapLengths.Add(distance);

                if (result.IsCircuit)
                {
                    result.LapCount = (int)Math.Round(result.LapNumber[n - 1]);

                    // Only laps long enough to be real contribute to the track
                    // length: the out lap includes pit-exit distance and the final
                    // lap is usually partial. Median over the rest is robust to both.
                    var full = lapLengths.Where(d => d > 500).OrderBy(d => d).ToList();
                    result.TrackLengthM = full.Count > 0 ? full[full.Count / 2] : lapLengths.Max();

                    var durations = new List<double>();
                    double previous = t.TimeS[0];
                    foreach (double end in lapEndTimes)
                    {
                        durations.Add(end - previous);
                        previous = end;
                    }
                    var fullDurations = durations.Where(d => d > 10).OrderBy(d => d).ToList();
                    result.EstLapTimeS = fullDurations.Count > 0
                        ? fullDurations[fullDurations.Count / 2]
                        : t.TimeS[n - 1] - t.TimeS[0];
                }
                else
                {
                    result.LapCount = 1;
                    result.TrackLengthM = distance;
                    result.EstLapTimeS = t.TimeS[n - 1] - t.TimeS[0];
                }

                if (result.TrackLengthM <= 0) result.TrackLengthM = 1.0;
                return result;
            }
        }

        // ------------------------------------------------------------------
        // Resampling and channel assembly
        // ------------------------------------------------------------------

        internal sealed class ResampledGrid
        {
            public List<IbtChannel> Channels = new List<IbtChannel>();
            public double DurationS;
            public int MaxGear;
        }

        internal static class Resampler
        {
            public static ResampledGrid Build(SampleTable t, LapAnalysis laps, int tickRateHz)
            {
                var grid = new ResampledGrid();
                double t0 = t.TimeS[0];
                double duration = t.TimeS[t.RowCount - 1] - t0;
                int outCount = (int)Math.Round(duration * tickRateHz) + 1;
                if (outCount < 1) outCount = 1;

                var outTimes = new double[outCount];
                for (int i = 0; i < outCount; i++) outTimes[i] = (double)i / tickRateHz;

                grid.DurationS = outTimes[outCount - 1];

                Func<List<double>, double, List<double>> lerp = (src, scale) =>
                    src == null ? Constant(0.0, outCount) : Interpolate(t.TimeS, t0, src, outTimes, scale);
                Func<List<double>, List<double>> hold = src =>
                    src == null ? Constant(0.0, outCount) : StepHold(t.TimeS, t0, src, outTimes);

                var speed = lerp(t.Column("Speed_kmh"), 1.0 / 3.6);
                // LapDist is continuous within a lap but drops to zero at each
                // boundary. Interpolating across that reset would ramp the value
                // down through the final fraction of every lap (a 99m -> 0 step
                // reads as 82m mid-way), so the drop is held instead.
                var lapDist = Interpolate(t.TimeS, t0, laps.LapDist, outTimes, 1.0,
                    holdAcrossDrops: true);
                var lapNumber = StepHold(t.TimeS, t0, laps.LapNumber, outTimes);
                var gear = hold(t.Column("Gear"));

                grid.MaxGear = gear.Count > 0 ? (int)Math.Round(gear.Max()) : 0;

                // LapDistPct is a 0..1 ratio. The out lap can exceed the measured
                // track length (it includes pit-exit distance), which would push
                // this past 1.0, so it is clamped rather than allowed to overflow.
                var lapDistPct = new List<double>(outCount);
                for (int i = 0; i < outCount; i++)
                {
                    double pct = lapDist[i] / laps.TrackLengthM;
                    lapDistPct.Add(pct < 0 ? 0 : (pct > 0.999999 ? 0.999999 : pct));
                }

                var lapCompleted = lapNumber.Select(v => Math.Max(0.0, v - 1)).ToList();
                var sessionTime = outTimes.ToList();
                var sessionTick = Enumerable.Range(0, outCount).Select(i => (double)i).ToList();

                var mapped = new Dictionary<string, List<double>>(StringComparer.Ordinal)
                {
                    { "SessionTime", sessionTime },
                    { "SessionTick", sessionTick },
                    { "Speed", speed },
                    { "RPM", lerp(t.Column("RPM"), 1.0) },
                    { "Gear", gear },
                    // Pedal channels carry unit "%" but are stored as 0..1 ratios,
                    // while shtep records them as 0-100 (a deliberate, live-confirmed
                    // passthrough). Hence the /100 here - the exact inverse of the
                    // original *100 assumption that was wrong.
                    { "Throttle", lerp(t.Column("Throttle_pct"), 0.01) },
                    { "Brake", lerp(t.Column("Brake_pct"), 0.01) },
                    { "Clutch", lerp(t.Column("Clutch_pct"), 0.01) },
                    { "FuelLevelPct", lerp(t.Column("FuelLevel_pct"), 0.01) },
                    { "LapDist", lapDist },
                    { "LapDistPct", lapDistPct },
                    { "Lap", lapNumber },
                    { "LapCompleted", lapCompleted },
                    // shtep's LatAccel_g/LongAccel_g/VertAccel_g are misnamed: the
                    // values are m/s^2, not g (a ±23 reading is impossible as g and
                    // correct as m/s^2). iRacing's channels are m/s^2 too, so these
                    // pass straight through. If the suffix is ever corrected, do NOT
                    // add a x9.81 conversion here - only the label was ever wrong.
                    { "LatAccel", lerp(t.Column("LatAccel_g"), 1.0) },
                    { "LongAccel", lerp(t.Column("LongAccel_g"), 1.0) },
                    { "VertAccel", lerp(t.Column("VertAccel_g"), 1.0) },
                    { "BrakeABSactive", hold(t.Column("ABSActive")) },
                    { "IsOnTrack", Constant(1.0, outCount) },
                };

                // Every variable in the set is emitted, whether or not we have data
                // for it. A file carrying only the channels we can fill is REJECTED
                // by Pi Toolbox - a 13-channel build produced no telemetry at all,
                // not merely missing metadata. The importer references 18 channels
                // by name and needs them present; unfilled ones are zeroed.
                foreach (var v in IbtChannelSet.All)
                {
                    List<double> values;
                    if (!mapped.TryGetValue(v.Name, out values)) values = Constant(0.0, outCount);
                    grid.Channels.Add(new IbtChannel(v, values));
                }

                return grid;
            }

            private static List<double> Constant(double value, int count)
            {
                var list = new List<double>(count);
                for (int i = 0; i < count; i++) list.Add(value);
                return list;
            }

            private static List<double> Interpolate(List<double> srcTimes, double t0,
                List<double> src, double[] outTimes, double scale, bool holdAcrossDrops = false)
            {
                var result = new List<double>(outTimes.Length);
                int j = 0;
                for (int i = 0; i < outTimes.Length; i++)
                {
                    double target = outTimes[i] + t0;
                    while (j + 1 < srcTimes.Count && srcTimes[j + 1] < target) j++;
                    if (j + 1 >= srcTimes.Count)
                    {
                        result.Add(src[src.Count - 1] * scale);
                        continue;
                    }
                    if (holdAcrossDrops && src[j + 1] < src[j])
                    {
                        result.Add(src[j] * scale);
                        continue;
                    }
                    double a = srcTimes[j], b = srcTimes[j + 1];
                    double w = (b == a) ? 0.0 : (target - a) / (b - a);
                    result.Add((src[j] + (src[j + 1] - src[j]) * w) * scale);
                }
                return result;
            }

            /// <summary>
            /// Nearest-prior-sample for discrete channels. Interpolating a gear or
            /// lap counter would invent values that never occurred (a "gear 2.4"
            /// between shifts).
            /// </summary>
            private static List<double> StepHold(List<double> srcTimes, double t0,
                List<double> src, double[] outTimes)
            {
                var result = new List<double>(outTimes.Length);
                int j = 0;
                for (int i = 0; i < outTimes.Length; i++)
                {
                    double target = outTimes[i] + t0;
                    while (j + 1 < srcTimes.Count && srcTimes[j + 1] <= target) j++;
                    result.Add(src[j]);
                }
                return result;
            }
        }
    }

    /// <summary>
    /// The variables written to every .ibt.
    ///
    /// The first 18 are those Pi Toolbox's importer references by name (extracted
    /// from its DLL string table); they must be present even when shtep has no
    /// data for them, or the file imports with no telemetry at all. The remainder
    /// are channels shtep can actually fill.
    ///
    /// Every name, type, unit and description is copied verbatim from a genuine
    /// iRacing capture - none of these strings are invented.
    /// </summary>
    internal static class IbtChannelSet
    {
        public static readonly IbtVar[] All =
        {
            // Referenced by the importer.
            new IbtVar("SessionTick", 2, "", "Current update number"),
            new IbtVar("DriverMarker", 1, "", "Driver activated flag"),
            new IbtVar("PlayerTrackSurface", 2, "irsdk_TrkLoc", "Players car track surface type"),
            new IbtVar("PlayerCarMyIncidentCount", 2, "", "Players own incident count for this session"),
            new IbtVar("SteeringWheelAngle", 4, "rad", "Steering wheel angle"),
            new IbtVar("RPM", 4, "revs/min", "Engine rpm"),
            new IbtVar("LapCompleted", 2, "", "Laps completed count"),
            new IbtVar("LapDist", 4, "m", "Meters traveled from S/F this lap"),
            new IbtVar("LapDistPct", 4, "%", "Percentage distance around lap"),
            new IbtVar("Speed", 4, "m/s", "GPS vehicle speed"),
            new IbtVar("YawNorth", 4, "rad", "Yaw orientation relative to north"),
            new IbtVar("Lat", 5, "deg", "Latitude in decimal degrees"),
            new IbtVar("Lon", 5, "deg", "Longitude in decimal degrees"),
            new IbtVar("Alt", 4, "m", "Altitude in meters"),
            new IbtVar("WindVel", 4, "m/s", "Wind velocity at start/finish line"),
            new IbtVar("VertAccel", 4, "m/s^2", "Vertical acceleration (including gravity)"),
            new IbtVar("LatAccel", 4, "m/s^2", "Lateral acceleration (including gravity)"),
            new IbtVar("LongAccel", 4, "m/s^2", "Longitudinal acceleration (including gravity)"),

            // Filled from shtep telemetry.
            new IbtVar("SessionTime", 5, "s", "Seconds since session start"),
            new IbtVar("Throttle", 4, "%", "0=off throttle to 1=full throttle"),
            new IbtVar("Brake", 4, "%", "0=brake released to 1=max pedal force"),
            new IbtVar("Clutch", 4, "%", "0=disengaged to 1=fully engaged"),
            new IbtVar("Gear", 2, "", "-1=reverse  0=neutral  1..n=current gear"),
            new IbtVar("Lap", 2, "", "Laps started count"),
            new IbtVar("IsOnTrack", 1, "", "1=Car on track physics running with player in car"),
            new IbtVar("FuelLevelPct", 4, "%", "Percent fuel remaining"),
            new IbtVar("BrakeABSactive", 1, "", "true if abs is currently reducing brake force pressure"),
        };
    }
}
