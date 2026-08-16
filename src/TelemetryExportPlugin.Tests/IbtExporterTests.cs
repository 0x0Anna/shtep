using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using TelemetryExportPlugin.Export;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    /// <summary>
    /// Structural and round-trip tests for the .ibt exporter. The layout constants
    /// asserted here were validated byte-for-byte against genuine iRacing captures
    /// and confirmed by importing generated files into Pi Toolbox itself - see
    /// PI_TOOLBOX_EXPORT.md.
    /// </summary>
    public class IbtExporterTests : IDisposable
    {
        private const int HeaderSize = 144;
        private const int VarHeaderSize = 144;

        private readonly string _dir;

        public IbtExporterTests()
        {
            _dir = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "tep_tests_ibt_" + Guid.NewGuid())).FullName;
        }

        public void Dispose()
        {
            Directory.Delete(_dir, recursive: true);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static RecordingSidecar Sidecar(string sessionType = "stint")
        {
            return new RecordingSidecar
            {
                Sim = "GranTurismo7",
                SessionType = sessionType,
                Context = "Test Circuit",
                Car = "Test Car",
                Driver = "Annalise",
                StartTimeUtc = "2026-08-04T23:31:16Z",
                EndTimeUtc = "2026-08-04T23:44:40Z",
                SampleRateHz = 100,
                PluginVersion = "0.1.0",
            };
        }

        private string WriteTsv(string baseName, string content)
        {
            string path = Path.Combine(_dir, $"{baseName}.tsv");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        /// <summary>Two laps at 10 Hz, constant 36 km/h (= 10 m/s) so distance is trivially checkable.</summary>
        private string TwoLapTsv(string baseName)
        {
            var sb = new StringBuilder();
            sb.Append("Time_s\tPaused\tDiscontinuity\tSpeed_kmh\tRPM\tGear\t")
              .Append("Throttle_pct\tBrake_pct\tClutch_pct\tFuelLevel_pct\tLapNumber\tABSActive\n");
            for (int i = 0; i <= 200; i++)
            {
                double t = i * 0.1;
                int lap = i < 100 ? 1 : 2;
                sb.Append(t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("\t0\t0\t36\t3000\t3\t50\t0\t0\t80\t").Append(lap).Append("\t0\n");
            }
            return WriteTsv(baseName, sb.ToString());
        }

        private sealed class ParsedIbt
        {
            public int Ver, Status, TickRate, SessionInfoLen, SessionInfoOffset;
            public int NumVars, VarHeaderOffset, NumBuf, BufLen, BufOffset;
            public int LapCount, RecordCount;
            public uint StartDate;
            public double EndTime;
            public string Yaml;
            public Dictionary<string, Tuple<int, int, string>> Vars =
                new Dictionary<string, Tuple<int, int, string>>(StringComparer.Ordinal);
            public byte[] Bytes;

            public double Value(string name, int record)
            {
                var v = Vars[name];
                int at = BufOffset + record * BufLen + v.Item2;
                switch (v.Item1)
                {
                    case 1: return Bytes[at];
                    case 2: return BitConverter.ToInt32(Bytes, at);
                    case 4: return BitConverter.ToSingle(Bytes, at);
                    case 5: return BitConverter.ToDouble(Bytes, at);
                    default: throw new NotSupportedException();
                }
            }
        }

        private static ParsedIbt Parse(string path)
        {
            var b = File.ReadAllBytes(path);
            var p = new ParsedIbt { Bytes = b };
            p.Ver = BitConverter.ToInt32(b, 0);
            p.Status = BitConverter.ToInt32(b, 4);
            p.TickRate = BitConverter.ToInt32(b, 8);
            p.SessionInfoLen = BitConverter.ToInt32(b, 16);
            p.SessionInfoOffset = BitConverter.ToInt32(b, 20);
            p.NumVars = BitConverter.ToInt32(b, 24);
            p.VarHeaderOffset = BitConverter.ToInt32(b, 28);
            p.NumBuf = BitConverter.ToInt32(b, 32);
            p.BufLen = BitConverter.ToInt32(b, 36);
            p.BufOffset = BitConverter.ToInt32(b, 52);
            p.StartDate = BitConverter.ToUInt32(b, 112);
            p.EndTime = BitConverter.ToDouble(b, 128);
            p.LapCount = BitConverter.ToInt32(b, 136);
            p.RecordCount = BitConverter.ToInt32(b, 140);
            p.Yaml = Encoding.UTF8.GetString(b, p.SessionInfoOffset, p.SessionInfoLen);

            for (int i = 0; i < p.NumVars; i++)
            {
                int o = p.VarHeaderOffset + i * VarHeaderSize;
                int type = BitConverter.ToInt32(b, o);
                int offset = BitConverter.ToInt32(b, o + 4);
                string name = Ascii(b, o + 16, 32);
                string unit = Ascii(b, o + 112, 32);
                p.Vars[name] = Tuple.Create(type, offset, unit);
            }
            return p;
        }

        private static string Ascii(byte[] b, int at, int width)
        {
            int end = at;
            while (end < at + width && b[end] != 0) end++;
            return Encoding.ASCII.GetString(b, at, end - at);
        }

        // ------------------------------------------------------------------
        // Structural layout
        // ------------------------------------------------------------------

        [Fact]
        public void Export_WritesThreeRegionsBackToBackWithNoGaps()
        {
            string baseName = "gt7_test_20260804_233116";
            string tsv = TwoLapTsv(baseName);

            string ibt = IbtExporter.Export(tsv, Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal(Path.Combine(_dir, $"{baseName}.ibt"), ibt);
            Assert.Equal(2, p.Ver);
            Assert.Equal(1, p.Status);
            Assert.Equal(1, p.NumBuf);
            Assert.Equal(HeaderSize, p.VarHeaderOffset);
            Assert.Equal(HeaderSize + p.NumVars * VarHeaderSize, p.SessionInfoOffset);
            Assert.Equal(p.SessionInfoOffset + p.SessionInfoLen, p.BufOffset);
            Assert.Equal(p.BufOffset + p.BufLen * p.RecordCount, p.Bytes.Length);
        }

        [Fact]
        public void Export_EmitsEveryReferencedChannel_EvenWithoutSourceData()
        {
            // A minimal var table is rejected by Pi Toolbox - a 13-channel file
            // imported with no telemetry at all. These 18 names are the ones its
            // converter references, and must be present regardless of whether
            // shtep has data for them.
            string[] required =
            {
                "SessionTick", "DriverMarker", "PlayerTrackSurface", "PlayerCarMyIncidentCount",
                "SteeringWheelAngle", "RPM", "LapCompleted", "LapDist", "LapDistPct", "Speed",
                "YawNorth", "Lat", "Lon", "Alt", "WindVel", "VertAccel", "LatAccel", "LongAccel",
            };

            string baseName = "gt7_required";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            foreach (var name in required)
            {
                Assert.True(p.Vars.ContainsKey(name), $"missing required channel '{name}'");
            }
        }

        [Fact]
        public void Export_VarOffsetsAreNaturallyAlignedAndDoNotOverlap()
        {
            string baseName = "gt7_align";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            var occupied = new HashSet<int>();
            foreach (var kv in p.Vars)
            {
                int type = kv.Value.Item1;
                int offset = kv.Value.Item2;
                int size = type == 1 ? 1 : (type == 5 ? 8 : 4);

                Assert.True(offset % size == 0,
                    $"'{kv.Key}' at {offset} is not {size}-byte aligned");
                Assert.True(offset + size <= p.BufLen,
                    $"'{kv.Key}' overruns the {p.BufLen}-byte record");

                for (int i = offset; i < offset + size; i++)
                {
                    Assert.True(occupied.Add(i), $"'{kv.Key}' overlaps another channel at byte {i}");
                }
            }
        }

        // ------------------------------------------------------------------
        // The time-axis trap
        // ------------------------------------------------------------------

        [Fact]
        public void Export_DeclaredTickRateAgreesWithRecordCountAndDuration()
        {
            // The failure this guards against passes every other structural check
            // and round-trips every value correctly, while stretching the session:
            // consumers reconstruct time as index * (1/tick_rate).
            string baseName = "gt7_tick";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName, 60);
            var p = Parse(ibt);

            Assert.Equal(60, p.TickRate);
            double impliedDuration = (double)p.RecordCount / p.TickRate;
            Assert.True(Math.Abs(impliedDuration - p.EndTime) < 0.05,
                $"record_count/tick_rate = {impliedDuration:F3}s but session_end_time = {p.EndTime:F3}s");
        }

        [Fact]
        public void Export_ReconstructedTimeAxisMatchesStoredSessionTimeChannel()
        {
            string baseName = "gt7_axis";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName, 60);
            var p = Parse(ibt);

            for (int r = 0; r < p.RecordCount; r += 7)
            {
                double reconstructed = (double)r / p.TickRate;
                double stored = p.Value("SessionTime", r);
                Assert.True(Math.Abs(reconstructed - stored) < 1e-6,
                    $"record {r}: header-derived {reconstructed:F6}s vs SessionTime {stored:F6}s");
            }
        }

        [Theory]
        [InlineData(30)]
        [InlineData(60)]
        [InlineData(100)]
        public void Export_HonoursRequestedTickRate(int tickRateHz)
        {
            string baseName = "gt7_rate_" + tickRateHz;
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName, tickRateHz);
            var p = Parse(ibt);

            Assert.Equal(tickRateHz, p.TickRate);
            // 20s of source at the requested rate, plus the inclusive final sample.
            Assert.Equal(20 * tickRateHz + 1, p.RecordCount);
        }

        // ------------------------------------------------------------------
        // Unit conversions
        // ------------------------------------------------------------------

        [Fact]
        public void Export_ConvertsPercentChannelsToRatios()
        {
            // Variables whose unit is "%" are stored 0..1 in the file, while shtep
            // records 0-100 - the exact inverse of the original *100 assumption.
            string baseName = "gt7_pct";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal("%", p.Vars["Throttle"].Item3);
            Assert.Equal(0.50, p.Value("Throttle", 30), 3);
            Assert.Equal(0.80, p.Value("FuelLevelPct", 30), 3);
        }

        [Fact]
        public void Export_ConvertsSpeedFromKmhToMetresPerSecond()
        {
            string baseName = "gt7_speed";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal("m/s", p.Vars["Speed"].Item3);
            Assert.Equal(10.0, p.Value("Speed", 30), 3);   // 36 km/h
        }

        [Fact]
        public void Export_PassesAccelerationChannelsThroughUnscaled()
        {
            // shtep's *_g columns are misnamed: the values are m/s^2, which is what
            // iRacing uses too. A x9.81 conversion here would be wrong.
            string baseName = "gt7_accel";
            string tsv = WriteTsv(baseName,
                "Time_s\tSpeed_kmh\tLatAccel_g\tLongAccel_g\tVertAccel_g\n" +
                "0.000\t36\t12.5\t-8.25\t9.81\n" +
                "0.100\t36\t12.5\t-8.25\t9.81\n" +
                "0.200\t36\t12.5\t-8.25\t9.81\n");

            string ibt = IbtExporter.Export(tsv, Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal("m/s^2", p.Vars["LatAccel"].Item3);
            Assert.Equal(12.5, p.Value("LatAccel", 5), 3);
            Assert.Equal(-8.25, p.Value("LongAccel", 5), 3);
            Assert.Equal(9.81, p.Value("VertAccel", 5), 3);
        }

        // ------------------------------------------------------------------
        // Lap handling
        // ------------------------------------------------------------------

        [Fact]
        public void Export_CircuitSession_ResetsLapDistAtEachLapBoundary()
        {
            // 10 m/s for 10s per lap => each lap is 100 m and LapDist restarts.
            string baseName = "gt7_laps";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName, 60);
            var p = Parse(ibt);

            Assert.Equal(2, p.LapCount);
            Assert.Equal(1.0, p.Value("Lap", 60), 3);
            Assert.Equal(2.0, p.Value("Lap", 660), 3);
            Assert.Equal(0.0, p.Value("LapCompleted", 60), 3);
            Assert.Equal(1.0, p.Value("LapCompleted", 660), 3);

            // End of lap 1 has accumulated ~100 m; just after the boundary it resets.
            Assert.True(p.Value("LapDist", 595) > 90,
                $"expected ~100 m at end of lap 1, got {p.Value("LapDist", 595):F1}");
            Assert.True(p.Value("LapDist", 610) < 20,
                $"expected a reset early in lap 2, got {p.Value("LapDist", 610):F1}");
        }

        [Fact]
        public void Export_StageSession_AccumulatesDistanceWithoutResetting()
        {
            // No LapNumber column at all - the rally case. Distance must run
            // cumulatively across the whole stage.
            string baseName = "acr_stage";
            var sb = new StringBuilder("Time_s\tSpeed_kmh\n");
            for (int i = 0; i <= 200; i++)
            {
                sb.Append((i * 0.1).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("\t36\n");
            }
            string tsv = WriteTsv(baseName, sb.ToString());

            string ibt = IbtExporter.Export(tsv, Sidecar("stage"), _dir, baseName, 60);
            var p = Parse(ibt);

            Assert.Equal(1, p.LapCount);
            Assert.True(p.Value("LapDist", p.RecordCount - 1) > 190,
                $"expected ~200 m cumulative, got {p.Value("LapDist", p.RecordCount - 1):F1}");
        }

        [Fact]
        public void Export_ClampsLapDistPctBelowOne()
        {
            // The out lap includes pit-exit distance and so exceeds the measured
            // track length; iRacing's LapDistPct is a 0..1 ratio.
            string baseName = "gt7_pct_clamp";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName, 60);
            var p = Parse(ibt);

            for (int r = 0; r < p.RecordCount; r += 13)
            {
                double pct = p.Value("LapDistPct", r);
                Assert.InRange(pct, 0.0, 1.0);
            }
        }

        // ------------------------------------------------------------------
        // Session YAML
        // ------------------------------------------------------------------

        [Fact]
        public void Export_YamlCarriesEveryKeyTheImporterNavigates()
        {
            string[] keys =
            {
                "TrackName:", "TrackLength:", "TrackLengthOfficial:", "TrackConfigName:",
                "TrackDisplayShortName:", "TrackDisplayName:", "SessionID:", "BuildVersion:",
                "BuildType:", "DriverCarIdx:", "DriverCarIsElectric:", "DriverCarEstLapTime:",
                "CarID:", "CarScreenName:", "UserName:", "IsSpectator:", "SessionNum:", "Sectors:",
            };

            string baseName = "gt7_yaml";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            foreach (var key in keys)
            {
                Assert.True(p.Yaml.Contains(key), $"session YAML is missing '{key}'");
            }
            Assert.StartsWith("---", p.Yaml);
            Assert.Contains("\r\n", p.Yaml);
            Assert.Contains("Test Circuit", p.Yaml);
            Assert.Contains("Annalise", p.Yaml);
        }

        [Fact]
        public void Export_SessionStartDateComesFromSidecarNotWallClock()
        {
            // Pi Toolbox surfaces this as the outing's "Create date"; inheriting it
            // from anywhere else mislabels the session.
            string baseName = "gt7_date";
            string ibt = IbtExporter.Export(TwoLapTsv(baseName), Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            var expected = new DateTimeOffset(2026, 8, 4, 23, 31, 16, TimeSpan.Zero).ToUnixTimeSeconds();
            Assert.Equal((uint)expected, p.StartDate);
        }

        [Fact]
        public void Export_SanitisesSimSuppliedNamesThatWouldBreakYaml()
        {
            string baseName = "gt7_sanitise";
            var sidecar = Sidecar();
            sidecar.Car = "Weird: Car\nName";
            sidecar.Context = "Track\twith\rjunk";

            string ibt = IbtExporter.Export(TwoLapTsv(baseName), sidecar, _dir, baseName);
            var p = Parse(ibt);

            int yamlStart = p.SessionInfoOffset;
            string carLine = null;
            foreach (var line in p.Yaml.Split('\n'))
            {
                if (line.Contains("CarScreenName:")) { carLine = line; break; }
            }
            Assert.NotNull(carLine);
            Assert.DoesNotContain("\n", carLine.TrimEnd('\r'));
            Assert.Contains("Weird- Car Name", p.Yaml);
            Assert.True(yamlStart > 0);
        }

        // ------------------------------------------------------------------
        // Duplicated columns and degenerate input
        // ------------------------------------------------------------------

        [Fact]
        public void Export_ReadsFirstOccurrenceOfDuplicatedColumns()
        {
            // Recorded files repeat their channel block N times (the SimHub
            // ReadCommonSettings list-append bug). Every repeat holds the same
            // values, so reading the first is correct - and a naive dictionary
            // build would silently take the last.
            string baseName = "gt7_dupes";
            string tsv = WriteTsv(baseName,
                "Time_s\tSpeed_kmh\tRPM\tSpeed_kmh\tRPM\n" +
                "0.000\t36\t3000\t36\t3000\n" +
                "0.100\t36\t3000\t36\t3000\n" +
                "0.200\t36\t3000\t36\t3000\n");

            string ibt = IbtExporter.Export(tsv, Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal(10.0, p.Value("Speed", 5), 3);
            Assert.Equal(3000.0, p.Value("RPM", 5), 3);
        }

        [Fact]
        public void Export_HoldsLastKnownValueAcrossEmptyCells()
        {
            string baseName = "gt7_gaps";
            string tsv = WriteTsv(baseName,
                "Time_s\tSpeed_kmh\tRPM\n" +
                "0.000\t36\t3000\n" +
                "0.100\t\t3000\n" +
                "0.200\t36\t\n");

            string ibt = IbtExporter.Export(tsv, Sidecar(), _dir, baseName);
            var p = Parse(ibt);

            Assert.Equal(10.0, p.Value("Speed", 5), 3);
        }

        [Fact]
        public void Export_ThrowsOnTsvWithoutTimeColumn()
        {
            string baseName = "gt7_notime";
            string tsv = WriteTsv(baseName, "Speed_kmh\tRPM\n36\t3000\n");

            var ex = Assert.Throws<InvalidOperationException>(
                () => IbtExporter.Export(tsv, Sidecar(), _dir, baseName));
            Assert.Contains("Time_s", ex.Message);
        }

        [Fact]
        public void Export_ThrowsOnTooFewRows()
        {
            string baseName = "gt7_short";
            string tsv = WriteTsv(baseName, "Time_s\tSpeed_kmh\n0.000\t36\n");

            Assert.Throws<InvalidOperationException>(
                () => IbtExporter.Export(tsv, Sidecar(), _dir, baseName));
        }

        [Fact]
        public void Export_RejectsNonPositiveTickRate()
        {
            string baseName = "gt7_badrate";
            string tsv = TwoLapTsv(baseName);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => IbtExporter.Export(tsv, Sidecar(), _dir, baseName, 0));
        }
    }
}
