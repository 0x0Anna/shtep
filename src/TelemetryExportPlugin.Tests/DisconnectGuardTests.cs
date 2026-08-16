using System;
using Xunit;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class DisconnectGuardTests
    {
        [Fact]
        public void NullData_ShorterThanGrace_DoesNotConfirmDisconnect()
        {
            var guard = new DisconnectGuard(graceMs: 3000);
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            Assert.False(guard.Feed(hasData: false, now: t0));
            Assert.False(guard.Feed(hasData: false, now: t0.AddMilliseconds(1000)));
            Assert.False(guard.Feed(hasData: false, now: t0.AddMilliseconds(2900)));
        }

        [Fact]
        public void NullData_HeldPastGrace_ConfirmsDisconnect()
        {
            var guard = new DisconnectGuard(graceMs: 3000);
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            Assert.False(guard.Feed(hasData: false, now: t0));
            Assert.True(guard.Feed(hasData: false, now: t0.AddMilliseconds(3000)));
        }

        [Fact]
        public void DataResumes_BeforeGraceExpires_ResetsStreak()
        {
            var guard = new DisconnectGuard(graceMs: 3000);
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            Assert.False(guard.Feed(hasData: false, now: t0));
            Assert.False(guard.Feed(hasData: false, now: t0.AddMilliseconds(1000)));
            Assert.False(guard.Feed(hasData: true, now: t0.AddMilliseconds(1500)));

            // A fresh null streak has to serve its own full grace window, not
            // continue counting from the streak that was just reset.
            Assert.False(guard.Feed(hasData: false, now: t0.AddMilliseconds(2000)));
            Assert.False(guard.Feed(hasData: false, now: t0.AddMilliseconds(4400)));
            Assert.True(guard.Feed(hasData: false, now: t0.AddMilliseconds(5000)));
        }

        [Fact]
        public void ConfirmedDisconnect_KeepsReturningTrue_WhileStillNull()
        {
            var guard = new DisconnectGuard(graceMs: 1000);
            var t0 = new DateTime(2026, 1, 1, 0, 0, 0);

            guard.Feed(hasData: false, now: t0);
            Assert.True(guard.Feed(hasData: false, now: t0.AddMilliseconds(1000)));
            Assert.True(guard.Feed(hasData: false, now: t0.AddMilliseconds(2000)));
        }
    }
}
