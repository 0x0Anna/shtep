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
        // How many samples after a lap change to keep suppressing the heuristic.
        // A single-sample skip (the old behavior) assumed the position field
        // snaps to its new-lap value on the same tick the lap counter increments.
        // Confirmed false live 2026-08-04 against a real GranTurismo7 session:
        // TrackPositionMeters kept reporting the old lap's trailing value for two
        // extra ticks after LapNumber flipped, then jumped ~5600m in a single
        // 10ms sample - past the single-sample guard, so the heuristic caught it
        // as a "backward rewind" and truncated most of a 16-minute race down to
        // 123s across repeated false triggers (one per lap). 5 samples (50ms at
        // the standard 100Hz rate) covers the observed 2-tick lag with margin.
        private const int LapChangeGraceSamples = 5;

        private readonly PluginSettings _settings;
        private double? _lastPosition;
        private double? _lastTimeS;
        private int _lapChangeGraceRemaining;

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
        /// <param name="lapJustChanged">
        /// True on the sample where the sim's lap counter just ticked over.
        /// LapDistance resets to ~0 at every lap boundary (SCHEMA.md), which is
        /// indistinguishable from a rewind by position delta alone - starts (or
        /// restarts) a <see cref="LapChangeGraceSamples"/>-sample grace window
        /// during which the heuristic is skipped, rather than misdetecting every
        /// ordinary lap completion as a backward rewind. `_lastPosition`/
        /// `_lastTimeS` still advance below regardless, so once the grace window
        /// ends the comparison is against wherever position actually settled.
        /// </param>
        /// <param name="rewindCapable">
        /// True only for sims confirmed to have a real player-facing rewind
        /// feature (SCHEMA.md's "Rewind handling" - Forza Horizon and similar).
        /// Anna's observation 2026-08-04: most sims have no such feature at all -
        /// GranTurismo7, for instance, can only restart a lap or session, never
        /// rewind mid-drive. A large backward position jump there is either a
        /// bad/noisy position signal or a genuine restart - never a "redo" the
        /// player is choosing to discard the preceding rows for. Truncating in
        /// that case is actively destructive (see the two rewind bugs this repo
        /// hit chasing exactly that). Defaults false (safer for any
        /// not-yet-confirmed sim): a heuristic-detected backward jump is
        /// classified as <see cref="DiscontinuityKind.Forward"/> instead of
        /// <see cref="DiscontinuityKind.Backward"/>, so it gets flagged and kept
        /// rather than truncated. Only sims with a confirmed real rewind feature
        /// should pass true.
        /// </param>
        public DiscontinuityKind Evaluate(double position, double timeS, bool simReportsResetOrAssist, bool lapJustChanged = false, bool rewindCapable = false)
        {
            DiscontinuityKind result = DiscontinuityKind.None;

            bool useSimEvent = _settings.DiscontinuityDetection == DiscontinuityDetectionMode.SimEvent
                || _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Both;
            bool useHeuristic = _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Heuristic
                || _settings.DiscontinuityDetection == DiscontinuityDetectionMode.Both;

            if (lapJustChanged)
            {
                _lapChangeGraceRemaining = LapChangeGraceSamples;
            }

            if (useSimEvent && simReportsResetOrAssist)
            {
                result = DiscontinuityKind.Forward;
            }

            bool inLapChangeGrace = _lapChangeGraceRemaining > 0;

            if (useHeuristic && !inLapChangeGrace && _lastPosition.HasValue && _lastTimeS.HasValue)
            {
                double dt = timeS - _lastTimeS.Value;
                if (dt > 0)
                {
                    double delta = position - _lastPosition.Value;
                    double impliedSpeedKmh = System.Math.Abs(delta) / dt * 3.6;

                    if (impliedSpeedKmh > _settings.HeuristicDiscontinuitySpeedKmh)
                    {
                        result = (delta < 0 && rewindCapable) ? DiscontinuityKind.Backward : DiscontinuityKind.Forward;
                    }
                }
            }

            if (inLapChangeGrace)
            {
                _lapChangeGraceRemaining--;
            }

            _lastPosition = position;
            _lastTimeS = timeS;

            return result;
        }

        public void Reset()
        {
            _lastPosition = null;
            _lastTimeS = null;
            _lapChangeGraceRemaining = 0;
        }
    }
}
