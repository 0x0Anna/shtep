using System;
using System.Collections.Generic;
using System.Timers;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Fixed-rate tick source, decoupled from SimHub's own DataUpdate() call
    /// rate (which tracks the sim's frame rate, not a fixed Hz). Callers feed
    /// current values in via UpdateChannel() as they arrive from DataUpdate;
    /// this class holds the last-known value for any channel that hasn't
    /// updated since the previous tick and emits one row per tick at
    /// SampleRateHz, per SCHEMA.md "Sampling".
    /// </summary>
    public class SampleTimer : IDisposable
    {
        private readonly System.Timers.Timer _timer;
        private readonly Dictionary<string, double> _lastKnown = new Dictionary<string, double>();
        private readonly object _lock = new object();

        // Guards the *entire* Tick() body, including the RowReady invocation below -
        // separate from _lock (which only protects the fast state fields touched by
        // UpdateChannel/SetPaused/SetDiscontinuity from SimHub's data thread).
        // System.Timers.Timer does NOT guarantee non-overlapping Elapsed callbacks:
        // if one Tick() runs longer than the tick interval (10ms at the default
        // 100Hz - trivially exceeded by a GC pause or disk I/O stall), the next
        // Elapsed fires concurrently on a second threadpool thread. Confirmed live
        // 2026-08-03: a real recording
        // (AssettoCorsaRally_Greece_Aghii_Theodori_20260803_204100.tsv) had 18
        // corrupted rows with byte-interleaved content from three different Time_s
        // values spliced into single lines - the signature of two Tick() calls both
        // reaching RecordingSession.WriteRow's unsynchronized _stream.Write
        // concurrently. RecordingSession.cs's own doc comment already says callers
        // "must serialize access to a single instance" - this lock is what actually
        // enforces that now, rather than relying on Tick() never overlapping.
        private readonly object _tickLock = new object();

        private long _rowIndex;
        private bool _paused;
        private bool _lastWrittenPaused;
        private bool _discontinuity;
        private bool _hasWrittenFirstRow;

        public int SampleRateHz { get; }

        /// <summary>Invoked once per emitted row with (Time_s, column values including Paused/Discontinuity).</summary>
        public event Action<double, IReadOnlyDictionary<string, double>> RowReady;

        public SampleTimer(int sampleRateHz)
        {
            SampleRateHz = sampleRateHz;
            _timer = new System.Timers.Timer(1000.0 / sampleRateHz) { AutoReset = true };
            _timer.Elapsed += OnElapsed;
        }

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        /// <summary>
        /// Rewinds the synthetic row counter after a RewindIndex truncation, so
        /// Time_s continues correctly from the point resumed rather than from
        /// wherever it was before the truncated rows existed.
        /// </summary>
        public void ResetRowIndexTo(long rowIndex)
        {
            lock (_lock) { _rowIndex = rowIndex; }
        }

        public void ResetForNewSession()
        {
            lock (_lock)
            {
                _rowIndex = 0;
                _lastWrittenPaused = false;
                _hasWrittenFirstRow = false;
                _discontinuity = false;
            }
        }

        public void UpdateChannel(string header, double value)
        {
            lock (_lock)
            {
                _lastKnown[header] = value;
            }
        }

        /// <summary>
        /// Set by the pause boundary logic. Cadence stops entirely while paused
        /// except for the single enter/exit transition rows (SCHEMA.md "Pause handling").
        /// </summary>
        public void SetPaused(bool paused)
        {
            lock (_lock) { _paused = paused; }
        }

        /// <summary>
        /// Set by DiscontinuityDetector/RewindIndex callers. Unlike Paused, this
        /// never stops sampling cadence - it only flags rows (SCHEMA.md "Discontinuity handling").
        /// </summary>
        public void SetDiscontinuity(bool discontinuity)
        {
            lock (_lock) { _discontinuity = discontinuity; }
        }

        private void OnElapsed(object sender, ElapsedEventArgs e) => Tick();

        /// <summary>
        /// The actual per-tick logic, factored out of the Timer.Elapsed handler so
        /// tests can drive it deterministically instead of racing a real timer.
        /// </summary>
        internal void Tick()
        {
            // Serializes the whole tick, including RowReady's invocation - see
            // _tickLock's comment above for why this is load-bearing, not just
            // defensive. A second overlapping Tick() blocks here until the first
            // one (including its downstream WriteRow) finishes, instead of both
            // reaching the file stream at once.
            lock (_tickLock)
            {
                Dictionary<string, double> snapshot;
                bool paused;
                bool discontinuity;
                bool isTransition;

                lock (_lock)
                {
                    paused = _paused;
                    discontinuity = _discontinuity;
                    isTransition = !_hasWrittenFirstRow || paused != _lastWrittenPaused;

                    if (paused && !isTransition)
                    {
                        // Cadence stopped for the duration of the pause; nothing to emit.
                        return;
                    }

                    snapshot = new Dictionary<string, double>(_lastKnown);
                    _lastWrittenPaused = paused;
                    _hasWrittenFirstRow = true;
                }

                snapshot["Paused"] = paused ? 1 : 0;
                snapshot["Discontinuity"] = discontinuity ? 1 : 0;

                double timeS = (double)_rowIndex / SampleRateHz;
                _rowIndex++;

                RowReady?.Invoke(timeS, snapshot);
            }
        }

        public void Dispose()
        {
            _timer.Elapsed -= OnElapsed;
            _timer.Dispose();
        }
    }
}
