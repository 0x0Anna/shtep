using TelemetryExportPlugin.Config;

namespace TelemetryExportPlugin.Recording
{
    public enum DiscontinuityKind
    {
        None,

        /// <summary>Forward jump - reset/assist/fast-travel. Flag Discontinuity=1, keep sampling.</summary>
        Forward,

        /// <summary>Backward jump - a rewind. Distinct handling: truncate rather than flag (SCHEMA.md).</summary>
        Backward,
    }

    /// <summary>
    /// Two detection tiers per SCHEMA.md "Discontinuity handling":
    ///   1. Sim-exposed event (caller passes simReportsResetOrAssist=true when the
    ///      SimHub adapter for the running sim exposes one - verify per-sim, not
    ///      all adapters do).
    ///   2. Heuristic fallback (always evaluated regardless of tier 1, since it's
    ///      the only signal at all for sims with no exposed event, e.g. Forza).
    ///
    /// Direction (forward vs backward) is what distinguishes a discontinuity
    /// (reset/assist/fast-travel) from a rewind - not a separate detector.
    /// </summary>
    public class DiscontinuityDetector
    {
        private readonly PluginSettings _settings;
        private double? _lastPosition;
        private double? _lastTimeS;

        public DiscontinuityDetector(PluginSettings settings)
        {
            _settings = settings;
        }

        /// <param name="position">World-space position or LapDistance fallback (meters).</param>
        /// <param name="timeS">Current row's Time_s.</param>
        /// <param name="simReportsResetOrAssist">
        /// True if the running sim's SimHub adapter exposes an explicit
        /// resetting/awaiting-assist state and it's currently active.
        /// </param>
        public DiscontinuityKind Evaluate(double position, double timeS, bool simReportsResetOrAssist)
        {
            DiscontinuityKind result = DiscontinuityKind.None;

            bool useSimEvent = _settings.DiscontinuityDetection == DiscontinuityDetectionMode.SimEvent
                || _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Both;
            bool useHeuristic = _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Heuristic
                || _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Both;

            if (useSimEvent && simReportsResetOrAssist)
            {
                result = DiscontinuityKind.Forward;
            }

            if (useHeuristic && _lastPosition.HasValue && _lastTimeS.HasValue)
            {
                double dt = timeS - _lastTimeS.Value;
                if (dt > 0)
                {
                    double delta = position - _lastPosition.Value;
                    double impliedSpeedKmh = System.Math.Abs(delta) / dt * 3.6;

                    if (impliedSpeedKmh > _settings.HeuristicDiscontinuitySpeedKmh)
                    {
                        result = delta < 0 ? DiscontinuityKind.Backward : DiscontinuityKind.Forward;
                    }
                }
            }

            _lastPosition = position;
            _lastTimeS = timeS;

            return result;
        }

        public void Reset()
        {
            _lastPosition = null;
            _lastTimeS = null;
        }
    }
}
