using System;

namespace TelemetryExportPlugin.Boundaries
{
    /// <summary>
    /// Circuit recording boundary: one file per stint, pit-exit to pit-entry,
    /// inferred from a boolean pit-lane property rather than a discrete event -
    /// hence the debounce, to avoid a momentary flicker near the pit line
    /// spawning a bogus stint file. See PLUGIN_IMPLEMENTATION_PLAN.md.
    ///
    /// Debounce logic itself is unit-testable (feed synthetic timestamps/bool
    /// ticks); the pit-lane boolean's actual StatusDataBase source property
    /// still needs verification per-sim against a live SimHub install before
    /// wiring Feed() up to real telemetry.
    /// </summary>
    public delegate void StintStartedHandler(string context, string car, string driver);

    public class CircuitBoundary
    {
        private readonly int _debounceMs;
        private bool _inPitLane;
        private bool _inStint;
        private bool? _pendingState;
        private DateTime _pendingSince;
        private bool _initialized;

        public event StintStartedHandler StintStarted;
        public event Action StintEnded;

        public CircuitBoundary(int debounceMs)
        {
            _debounceMs = debounceMs;
        }

        /// <summary>
        /// Re-arms the boundary so the next Feed() re-baselines from scratch, as if
        /// freshly constructed - same "first observed tick" path as the constructor.
        /// Needed whenever a session ends through a route other than this boundary's
        /// own StintEnded (e.g. a disconnect mid-stint via Plugin.cs's
        /// DisconnectGuard): without this, _inStint stays true from the stint that
        /// just got force-closed, so a reconnect reporting the same rawInPitLane
        /// value as before reads as "no change" and Feed() silently never fires
        /// StintStarted again - confirmed live 2026-08-14, FH6's UDP telemetry link
        /// flaps constantly, and every reconnect after the first disconnect recorded
        /// nothing until IsInPitLane happened to genuinely toggle.
        /// </summary>
        public void Reset()
        {
            _initialized = false;
            _inStint = false;
            _pendingState = null;
        }

        /// <summary>Call once per tick with the sim's current raw pit-lane boolean.</summary>
        public void Feed(bool rawInPitLane, DateTime now, string context, string car, string driver)
        {
            if (!_initialized)
            {
                // First observed tick establishes the baseline state rather than
                // requiring a pit->track transition, since the plugin can load (or
                // SimHub can start) mid-drive with the car already on track - no
                // transition will ever occur in that case, and a stint would never
                // start otherwise. Confirmed live against FH6: plugin loaded with
                // isInPitLane already false the entire session, no stint ever began.
                _initialized = true;
                _inPitLane = rawInPitLane;
                if (!_inPitLane)
                {
                    _inStint = true;
                    StintStarted?.Invoke(context, car, driver);
                }
                return;
            }

            if (rawInPitLane == _inPitLane)
            {
                _pendingState = null;
                return;
            }

            if (_pendingState != rawInPitLane)
            {
                _pendingState = rawInPitLane;
                _pendingSince = now;
                return;
            }

            if ((now - _pendingSince).TotalMilliseconds < _debounceMs)
            {
                return;
            }

            _inPitLane = rawInPitLane;
            _pendingState = null;

            if (_inPitLane && _inStint)
            {
                _inStint = false;
                StintEnded?.Invoke();
            }
            else if (!_inPitLane && !_inStint)
            {
                _inStint = true;
                StintStarted?.Invoke(context, car, driver);
            }
        }
    }
}
