using System;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Debounces data.NewData == null so a momentary telemetry gap doesn't read as
    /// a real disconnect. Confirmed live 2026-08-07 (GranTurismo7, see
    /// project-shtep-status memory for the full log trace): SimHub's data.NewData
    /// goes null for under a second around an in-game pause, but the old guard in
    /// Plugin.cs ended the session on the very first null tick - since the
    /// SimHubRecording trigger's d.ReplayMode was still "Record" the instant data
    /// resumed, a new session reopened almost immediately after, fragmenting one
    /// continuous drive into several files across every pause that night. Same
    /// debounce shape as CircuitBoundary's pit-lane transition.
    /// </summary>
    public class DisconnectGuard
    {
        private readonly int _graceMs;
        private DateTime? _nullSince;

        public DisconnectGuard(int graceMs)
        {
            _graceMs = graceMs;
        }

        /// <summary>
        /// Call once per tick with whether this tick had real data. Returns true
        /// once a null streak has persisted past the grace window - callers should
        /// treat that as a confirmed disconnect. Keeps returning true on every
        /// subsequent null tick (callers already guard against ending an
        /// already-closed session, so no edge-trigger latch is needed here).
        /// </summary>
        public bool Feed(bool hasData, DateTime now)
        {
            if (hasData)
            {
                _nullSince = null;
                return false;
            }

            if (_nullSince == null)
            {
                _nullSince = now;
                return false;
            }

            return (now - _nullSince.Value).TotalMilliseconds >= _graceMs;
        }
    }
}
