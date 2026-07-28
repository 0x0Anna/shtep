using System;
using System.IO;
using Xunit;
using TelemetryExportPlugin.Config;

namespace TelemetryExportPlugin.Tests
{
    public class PathValidationTests
    {
        [Fact]
        public void ValidateWritableDirectory_CreatesMissingDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "tep_tests_pv_" + Guid.NewGuid());
            try
            {
                Assert.False(Directory.Exists(path));
                var result = PathValidation.ValidateWritableDirectory(path, "TempDir");
                Assert.True(result.Success);
                Assert.True(Directory.Exists(path));
            }
            finally
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
        }

        [Fact]
        public void ValidateWritableDirectory_FailsClearly_ForEmptyPath()
        {
            var result = PathValidation.ValidateWritableDirectory("", "OutputDir");
            Assert.False(result.Success);
            Assert.Contains("OutputDir", result.Message);
        }

        [Fact]
        public void ValidateWritableDirectory_FailsClearly_ForUnreachableDrive()
        {
            // A drive letter vanishingly unlikely to exist on the test machine,
            // simulating "USB drive unplugged" / "network share unmounted".
            var result = PathValidation.ValidateWritableDirectory(@"Q:\definitely-not-mounted\telemetry", "TempDir");
            Assert.False(result.Success);
        }
    }
}
