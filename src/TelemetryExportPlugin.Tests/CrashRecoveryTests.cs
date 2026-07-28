using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Xunit;
using TelemetryExportPlugin.Config;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class CrashRecoveryTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _outputDir;
        private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

        public CrashRecoveryTests()
        {
            _tempDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tep_tests_crash_temp_" + Guid.NewGuid())).FullName;
            _outputDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tep_tests_crash_out_" + Guid.NewGuid())).FullName;
        }

        public void Dispose()
        {
            Directory.Delete(_tempDir, recursive: true);
            Directory.Delete(_outputDir, recursive: true);
        }

        private string WriteFixture(string baseName, bool truncateMidRow)
        {
            var path = Path.Combine(_tempDir, $"{baseName}.tsv.partial");
            var lines = "Time_s\tSpeed_kmh\n0.000\t10\n0.010\t20\n0.020\t30\n";
            if (truncateMidRow)
            {
                lines += "0.030\t4"; // cut off mid-row, no trailing newline, wrong column shape once split
            }
            File.WriteAllText(path, lines, Utf8NoBom);
            return path;
        }

        [Fact]
        public void RecoverAll_TruncatesPartialFinalRow_AndFlagsRecoveredFromCrash()
        {
            string baseName = "rbr_stage1_20260727_120000";
            WriteFixture(baseName, truncateMidRow: true);

            var settings = new PluginSettings { PurgeIncompleteOnStartup = false };
            CrashRecovery.RecoverAll(_tempDir, _outputDir, settings, "0.1.0");

            var dataPath = Path.Combine(_outputDir, $"{baseName}.tsv");
            var sidecarPath = Path.Combine(_outputDir, $"{baseName}.meta.json");
            Assert.True(File.Exists(dataPath));
            Assert.True(File.Exists(sidecarPath));

            var dataLines = File.ReadAllLines(dataPath);
            Assert.Equal(4, dataLines.Length); // header + 3 complete rows; truncated row dropped

            var sidecar = JObject.Parse(File.ReadAllText(sidecarPath));
            Assert.True((bool)sidecar["recoveredFromCrash"]);
        }

        [Fact]
        public void RecoverAll_PurgesOrphanedPartial_WhenConfigured()
        {
            string baseName = "rbr_stage1_20260727_120000";
            var partialPath = WriteFixture(baseName, truncateMidRow: false);

            var settings = new PluginSettings { PurgeIncompleteOnStartup = true };
            CrashRecovery.RecoverAll(_tempDir, _outputDir, settings, "0.1.0");

            Assert.False(File.Exists(partialPath));
            Assert.Empty(Directory.GetFiles(_outputDir));
        }
    }
}
