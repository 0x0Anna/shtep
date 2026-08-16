using System;
using Xunit;
using TelemetryExportPlugin.Boundaries;

namespace TelemetryExportPlugin.Tests
{
    public class CircuitBoundaryTests
    {
        [Fact]
        public void MomentaryFlicker_ShorterThanDebounce_DoesNotStartOrEndStint()
        {
            var boundary = new CircuitBoundary(debounceMs: 1000);
            bool started = false, ended = false;
            boundary.StintStarted += (c, car, driver) => started = true;
            boundary.StintEnded += () => ended = true;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            boundary.Feed(rawInPitLane: true, now: t0, context: "Track", car: "Car", driver: "Driver");
            boundary.Feed(rawInPitLane: false, now: t0.AddMilliseconds(200), context: "Track", car: "Car", driver: "Driver");
            boundary.Feed(rawInPitLane: true, now: t0.AddMilliseconds(400), context: "Track", car: "Car", driver: "Driver");

            Assert.False(started);
            Assert.False(ended);
        }

        /// <summary>Two ticks >= debounceMs apart with the same raw value locks that value in as confirmed state.</summary>
        private static void ConfirmPitLaneState(CircuitBoundary boundary, bool inPitLane, DateTime at)
        {
            boundary.Feed(inPitLane, at, "Track", "Car", "Driver");
            boundary.Feed(inPitLane, at.AddMilliseconds(1200), "Track", "Car", "Driver");
        }

        [Fact]
        public void PitExit_HeldPastDebounce_StartsStint()
        {
            var boundary = new CircuitBoundary(debounceMs: 1000);
            bool started = false;
            boundary.StintStarted += (c, car, driver) => started = true;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            ConfirmPitLaneState(boundary, inPitLane: true, at: t0); // confirmed: in pit lane

            boundary.Feed(false, t0.AddSeconds(5), "Track", "Car", "Driver");
            boundary.Feed(false, t0.AddSeconds(6.2), "Track", "Car", "Driver"); // held past debounce

            Assert.True(started);
        }

        [Fact]
        public void AlreadyOnTrack_AtFirstTick_StartsStintImmediately()
        {
            // Regression test: plugin/SimHub can start mid-drive with the car already
            // on track. Confirmed live against FH6 - isInPitLane was false from the
            // very first tick for the whole session, and the old code (which defaulted
            // _inPitLane to false and only fired on a true->false transition) never
            // saw a transition, so no stint - and therefore no recording - ever began.
            var boundary = new CircuitBoundary(debounceMs: 1000);
            bool started = false;
            boundary.StintStarted += (c, car, driver) => started = true;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            boundary.Feed(rawInPitLane: false, now: t0, context: "Track", car: "Car", driver: "Driver");

            Assert.True(started);
        }

        [Fact]
        public void AlreadyInPitLane_AtFirstTick_DoesNotStartStintUntilExit()
        {
            var boundary = new CircuitBoundary(debounceMs: 1000);
            bool started = false;
            boundary.StintStarted += (c, car, driver) => started = true;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            boundary.Feed(rawInPitLane: true, now: t0, context: "Track", car: "Car", driver: "Driver");

            Assert.False(started);
        }

        [Fact]
        public void PitEntry_HeldPastDebounce_EndsStint()
        {
            var boundary = new CircuitBoundary(debounceMs: 1000);
            bool ended = false;
            boundary.StintEnded += () => ended = true;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            ConfirmPitLaneState(boundary, inPitLane: true, at: t0); // confirmed: in pit lane

            boundary.Feed(false, t0.AddSeconds(5), "Track", "Car", "Driver");
            boundary.Feed(false, t0.AddSeconds(6.2), "Track", "Car", "Driver"); // stint starts

            boundary.Feed(true, t0.AddSeconds(60), "Track", "Car", "Driver");
            boundary.Feed(true, t0.AddSeconds(61.2), "Track", "Car", "Driver"); // held past debounce

            Assert.True(ended);
        }

        [Fact]
        public void Reset_AfterStintForceClosedWithoutPitLaneChange_StartsNewStintOnNextTick()
        {
            // Regression test: a session can be force-closed by something other than
            // a pit-lane transition (Plugin.cs's DisconnectGuard, on a real
            // disconnect). Without Reset(), _inStint stays true from the old stint,
            // so a reconnect reporting the same rawInPitLane value as before reads as
            // "no change" and Feed() never fires StintStarted again. Confirmed live
            // 2026-08-14 against FH6, whose UDP telemetry link disconnects/reconnects
            // frequently mid-drive.
            var boundary = new CircuitBoundary(debounceMs: 1000);
            int startedCount = 0;
            boundary.StintStarted += (c, car, driver) => startedCount++;

            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);
            boundary.Feed(rawInPitLane: false, now: t0, context: "Track", car: "Car", driver: "Driver");
            Assert.Equal(1, startedCount);

            // Simulate Plugin.cs's EndSession() force-closing the session on
            // disconnect, then the game reconnecting still on track (rawInPitLane
            // unchanged from before the disconnect).
            boundary.Reset();
            boundary.Feed(rawInPitLane: false, now: t0.AddSeconds(5), context: "Track", car: "Car", driver: "Driver");

            Assert.Equal(2, startedCount);
        }
    }
}
