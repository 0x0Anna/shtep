using System.Collections.Generic;
using Xunit;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class SampleTimerTests
    {
        [Fact]
        public void Tick_HoldsLastKnownValue_ForChannelNotUpdatedSinceLastTick()
        {
            var timer = new SampleTimer(100);
            var rows = new List<IReadOnlyDictionary<string, double>>();
            timer.RowReady += (t, values) => rows.Add(values);

            timer.UpdateChannel("Speed_kmh", 50);
            timer.Tick(); // row 0: Speed_kmh = 50

            timer.Tick(); // row 1: Speed_kmh not updated again - should hold 50

            Assert.Equal(50, rows[0]["Speed_kmh"]);
            Assert.Equal(50, rows[1]["Speed_kmh"]);
        }

        [Fact]
        public void Tick_EmitsSyntheticTimeS_AsRowIndexOverSampleRate()
        {
            var timer = new SampleTimer(100);
            var times = new List<double>();
            timer.RowReady += (t, values) => times.Add(t);

            timer.Tick();
            timer.Tick();
            timer.Tick();

            Assert.Equal(new[] { 0.0, 0.01, 0.02 }, times);
        }

        [Fact]
        public void Pause_StopsCadence_ExceptForSingleEnterExitTransitionRows()
        {
            var timer = new SampleTimer(100);
            var rows = new List<(double time, IReadOnlyDictionary<string, double> values)>();
            timer.RowReady += (t, values) => rows.Add((t, values));

            timer.Tick(); // normal row, Paused=0

            timer.SetPaused(true);
            timer.Tick(); // pause-enter transition row, Paused=1
            timer.Tick(); // mid-pause - cadence stopped, no row
            timer.Tick(); // mid-pause - cadence stopped, no row

            timer.SetPaused(false);
            timer.Tick(); // pause-exit transition row, Paused=0

            Assert.Equal(3, rows.Count);
            Assert.Equal(0, rows[0].values["Paused"]);
            Assert.Equal(1, rows[1].values["Paused"]);
            Assert.Equal(0, rows[2].values["Paused"]);
        }

        [Fact]
        public void Discontinuity_DoesNotStopCadence_UnlikePause()
        {
            var timer = new SampleTimer(100);
            var rows = new List<IReadOnlyDictionary<string, double>>();
            timer.RowReady += (t, values) => rows.Add(values);

            timer.SetDiscontinuity(true);
            timer.Tick();
            timer.Tick();
            timer.Tick();

            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(1, r["Discontinuity"]));
        }

        [Fact]
        public void ResetRowIndexTo_ResumesTimeSFromTruncationPoint()
        {
            var timer = new SampleTimer(100);
            var times = new List<double>();
            timer.RowReady += (t, values) => times.Add(t);

            timer.Tick();
            timer.Tick();
            timer.Tick(); // rows 0,1,2 -> t = 0.00, 0.01, 0.02

            timer.ResetRowIndexTo(1); // simulate a rewind truncation back to row 1
            timer.Tick(); // should resume at row 1 -> t = 0.01

            Assert.Equal(0.01, times[3]);
        }
    }
}
