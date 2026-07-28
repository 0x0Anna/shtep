using TelemetryExportPlugin.Config;
using TelemetryExportPlugin.Recording;
using Xunit;

namespace TelemetryExportPlugin.Tests
{
    public class DiscontinuityDetectorTests
    {
        [Fact]
        public void LapDistanceResetOnLapChange_IsNotMisdetectedAsBackwardRewind()
        {
            // Regression: LapDistance_m resets to ~0 at every lap boundary
            // (SCHEMA.md), which looks identical to a rewind by position delta
            // alone. Without lapJustChanged, this used to fire Backward on every
            // ordinary lap completion in a circuit recording.
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.0, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: 5, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: true);

            Assert.Equal(DiscontinuityKind.None, kind);
        }

        [Fact]
        public void SameJumpWithoutLapJustChanged_IsStillDetectedAsBackwardRewind()
        {
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.0, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: 5, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: false);

            Assert.Equal(DiscontinuityKind.Backward, kind);
        }

        [Fact]
        public void SuppressedSample_StillAdvancesBaseline_SoNextSampleComparesCorrectly()
        {
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.0, simReportsResetOrAssist: false);
            detector.Evaluate(position: 5, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: true);

            // Next sample is ordinary forward progress on the new lap - must not
            // be compared against the pre-reset (~4990m) position.
            var kind = detector.Evaluate(position: 5.5, timeS: 99.02, simReportsResetOrAssist: false);

            Assert.Equal(DiscontinuityKind.None, kind);
        }

        [Fact]
        public void ForwardJump_IsDetectedAsDiscontinuity()
        {
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 100, timeS: 1.0, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: 5000, timeS: 1.01, simReportsResetOrAssist: false);

            Assert.Equal(DiscontinuityKind.Forward, kind);
        }
    }
}
