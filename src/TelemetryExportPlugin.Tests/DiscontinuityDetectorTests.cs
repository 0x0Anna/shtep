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
        public void SameJumpWithoutLapJustChanged_IsStillDetectedAsBackwardRewind_ForRewindCapableSim()
        {
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.0, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: 5, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: false, rewindCapable: true);

            Assert.Equal(DiscontinuityKind.Backward, kind);
        }

        [Fact]
        public void BackwardJump_ForNonRewindCapableSim_IsClassifiedAsForwardNotBackward()
        {
            // Anna's observation 2026-08-04: GranTurismo7 (and most sims) have no
            // real rewind feature, only lap/session restart - a backward position
            // jump there is never a "redo" to discard prior rows for, so it must
            // not trigger RewindIndex truncation. rewindCapable defaults false.
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.0, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: 5, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: false);

            Assert.Equal(DiscontinuityKind.Forward, kind);
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

        [Fact]
        public void DelayedPositionResetAfterLapChange_IsNotMisdetectedAsBackwardRewind()
        {
            // Regression for a real GranTurismo7 race capture (2026-08-04):
            // TrackPositionMeters didn't snap to its new-lap value until two
            // samples after LapNumber incremented - it kept reporting the old
            // lap's trailing position in between. A single-sample lapJustChanged
            // skip missed this and truncated most of a 16-minute race.
            var detector = new DiscontinuityDetector(new PluginSettings());

            detector.Evaluate(position: 4990, timeS: 99.00, simReportsResetOrAssist: false);
            detector.Evaluate(position: 4990, timeS: 99.01, simReportsResetOrAssist: false, lapJustChanged: true);
            detector.Evaluate(position: 4990, timeS: 99.02, simReportsResetOrAssist: false);
            var kind = detector.Evaluate(position: -5633, timeS: 99.03, simReportsResetOrAssist: false);

            Assert.Equal(DiscontinuityKind.None, kind);
        }

        [Fact]
        public void BackwardJump_LongAfterLapChangeGraceWindow_IsStillDetected_ForRewindCapableSim()
        {
            // The grace window must actually end - a genuine rewind well after a
            // lap change (not just the reset settling) should still be caught.
            var detector = new DiscontinuityDetector(new PluginSettings());

            double t = 99.00;
            detector.Evaluate(position: 10, timeS: t, simReportsResetOrAssist: false, lapJustChanged: true);
            for (int i = 0; i < 10; i++)
            {
                t += 0.01;
                detector.Evaluate(position: 10 + i, timeS: t, simReportsResetOrAssist: false);
            }

            t += 0.01;
            var kind = detector.Evaluate(position: 5, timeS: t, simReportsResetOrAssist: false, rewindCapable: true);

            Assert.Equal(DiscontinuityKind.Backward, kind);
        }
    }
}
