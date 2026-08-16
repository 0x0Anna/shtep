using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Xunit;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class RecordingSessionTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _outputDir;

        public RecordingSessionTests()
        {
            _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tep_tests_temp_" + Guid.NewGuid())).FullName;
            _outputDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tep_tests_out_" + Guid.NewGuid())).FullName;
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
            Directory.Delete(_outputDir, recursive: true);
        }

        [Fact]
        public void Open_WritesHeaderRow_NoBomLfEndings()
        {
            var session = new RecordingSession(_tempDir, _outputDir, "rbr_stage1_20260727_120000", new[] { "Speed_kmh", "RPM" });
            session.Open();
            session.WriteRow(0.0, new Dictionary<string, double> { ["Paused"] = 0, ["Discontinuity"] = 0, ["Speed_kmh"] = 12.5, ["RPM"] = 3000 });
            session.Dispose(); // release the handle - a real reader only ever sees this file after Close() anyway

            var bytes = File.ReadAllBytes(session.PartialPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "file must not have a UTF-8 BOM");

            var text = File.ReadAllText(session.PartialPath, new System.Text.UTF8Encoding(false));
            Assert.DoesNotContain("\r\n", text);
            var lines = text.TrimEnd('\n').Split('\n');
            // "Paused"/"Discontinuity" are always present regardless of the
            // configured Channels list (SCHEMA.md - not conditional on channel
            // selection).
            Assert.Equal("Time_s\tPaused\tDiscontinuity\tSpeed_kmh\tRPM", lines[0]);
            Assert.Equal("0.000\t0\t0\t12.5\t3000", lines[1]);
        }

        [Fact]
        public void Close_MovesSidecarBeforeDataFile()
        {
            var session = new RecordingSession(_tempDir, _outputDir, "rbr_stage1_20260727_120000", new[] { "Speed_kmh" });
            session.Open();
            session.WriteRow(0.0, new Dictionary<string, double> { ["Speed_kmh"] = 1 });

            var events = new List<string>();
            using (var watcher = new FileSystemWatcher(_outputDir) { EnableRaisingEvents = true })
            {
                watcher.Created += (s, e) => { lock (events) events.Add(e.Name); };

                session.Close(new RecordingSidecar
                {
                    Sim = "rbr",
                    SessionType = "stage",
                    Context = "Stage 1",
                    StartTimeUtc = "2026-07-27T12:00:00Z",
                    EndTimeUtc = "2026-07-27T12:05:00Z",
                    SampleRateHz = 100,
                    Channels = new List<string> { "Speed_kmh" },
                    PluginVersion = "0.1.0",
                });

                // FileSystemWatcher events are async; give them a moment to land.
                System.Threading.Thread.Sleep(200);
            }

            Assert.True(File.Exists(Path.Combine(_outputDir, "rbr_stage1_20260727_120000.meta.json")));
            Assert.True(File.Exists(Path.Combine(_outputDir, "rbr_stage1_20260727_120000.tsv")));

            var sidecarIndex = events.IndexOf("rbr_stage1_20260727_120000.meta.json");
            var dataIndex = events.IndexOf("rbr_stage1_20260727_120000.tsv");
            Assert.True(sidecarIndex >= 0 && dataIndex >= 0, "expected FileSystemWatcher to observe both file creations");
            Assert.True(sidecarIndex < dataIndex, "sidecar must land in OutputDir before the data file, per SCHEMA.md write lifecycle");
        }

        [Fact]
        public void Close_WithDiscontinuitiesAndRewinds_SerializesArraysInSidecar()
        {
            var session = new RecordingSession(_tempDir, _outputDir, "fh6_freeroam_20260727_120000", new[] { "Speed_kmh" });
            session.Open();
            session.WriteRow(0.0, new Dictionary<string, double> { ["Speed_kmh"] = 1 });

            session.Close(new RecordingSidecar
            {
                Sim = "fh6",
                SessionType = "stint",
                Context = "Freeroam",
                StartTimeUtc = "2026-07-27T12:00:00Z",
                EndTimeUtc = "2026-07-27T12:05:00Z",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh" },
                Discontinuities = new List<DiscontinuityEntry>
                {
                    new DiscontinuityEntry { StartTimeS = 42.10, EndTimeS = 47.35, Reason = "unknown" },
                },
                Rewinds = new List<RewindEntry>
                {
                    new RewindEntry { TruncatedFromTimeS = 58.20, TruncatedToTimeS = 31.00, RowsRemoved = 2720 },
                },
                PluginVersion = "0.1.0",
            });

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "fh6_freeroam_20260727_120000.meta.json")));

            var discontinuity = Assert.Single(json["discontinuities"]);
            Assert.Equal(42.10, (double)discontinuity["startTimeS"]);
            Assert.Equal(47.35, (double)discontinuity["endTimeS"]);
            Assert.Equal("unknown", (string)discontinuity["reason"]);

            var rewind = Assert.Single(json["rewinds"]);
            Assert.Equal(58.20, (double)rewind["truncatedFromTimeS"]);
            Assert.Equal(31.00, (double)rewind["truncatedToTimeS"]);
            Assert.Equal(2720, (int)rewind["rowsRemoved"]);
        }

        [Fact]
        public void Close_WithoutDiscontinuitiesOrRewinds_OmitsArraysFromSidecar()
        {
            var session = new RecordingSession(_tempDir, _outputDir, "fh6_freeroam_20260727_130000", new[] { "Speed_kmh" });
            session.Open();
            session.WriteRow(0.0, new Dictionary<string, double> { ["Speed_kmh"] = 1 });

            session.Close(new RecordingSidecar
            {
                Sim = "fh6",
                SessionType = "stint",
                Context = "Freeroam",
                StartTimeUtc = "2026-07-27T13:00:00Z",
                EndTimeUtc = "2026-07-27T13:05:00Z",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh" },
                PluginVersion = "0.1.0",
            });

            var json = JObject.Parse(File.ReadAllText(Path.Combine(_outputDir, "fh6_freeroam_20260727_130000.meta.json")));

            Assert.Null(json["discontinuities"]);
            Assert.Null(json["rewinds"]);
        }

        [Fact]
        public void Discard_DeletesPartialFile_WritesNothingToOutputDir()
        {
            var session = new RecordingSession(_tempDir, _outputDir, "fh6_stub_20260815_230307", new[] { "Speed_kmh" });
            session.Open();
            session.WriteRow(0.0, new Dictionary<string, double> { ["Speed_kmh"] = 1 });

            var partialPath = session.PartialPath;
            Assert.True(File.Exists(partialPath));

            session.Discard();

            Assert.False(File.Exists(partialPath));
            Assert.False(session.IsOpen);
            Assert.Empty(Directory.GetFiles(_outputDir));
        }
    }
}
