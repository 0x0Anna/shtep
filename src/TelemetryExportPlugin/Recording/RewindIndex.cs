using System.Collections.Generic;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Lightweight in-memory (position, byteOffset) index maintained while a
    /// RecordingSession is being written, used to truncate-and-resume on rewind
    /// detection. See SCHEMA.md "Rewind handling".
    ///
    /// Key on world-space position where the sim exposes it; LapDistance is a
    /// fallback only, since it resets at lap boundaries and can misidentify the
    /// truncation point for a rewind spanning a lap crossing (known v1 limitation,
    /// not solved further per PLUGIN_IMPLEMENTATION_PLAN.md).
    /// </summary>
    public class RewindIndex
    {
        public struct Entry
        {
            public double Position;
            public long ByteOffset;
            public long RowIndex;
            public double TimeS;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>Record every row, or every Nth row - caller's choice of density/overhead tradeoff.</summary>
        public void Add(double position, long byteOffset, long rowIndex, double timeS)
        {
            _entries.Add(new Entry { Position = position, ByteOffset = byteOffset, RowIndex = rowIndex, TimeS = timeS });
        }

        /// <summary>
        /// Finds the latest indexed entry at or before the position the game
        /// rewound to. Returns false if the index is empty (nothing to truncate to).
        /// </summary>
        public bool TryFindTruncationPoint(double targetPosition, out Entry match)
        {
            match = default;
            bool found = false;

            foreach (var entry in _entries)
            {
                if (entry.Position <= targetPosition)
                {
                    if (!found || entry.Position > match.Position)
                    {
                        match = entry;
                        found = true;
                    }
                }
            }

            // No entry at/before target (e.g. rewind past the start of this file's
            // recorded range) - fall back to the earliest entry we have.
            if (!found && _entries.Count > 0)
            {
                match = _entries[0];
                found = true;
            }

            return found;
        }

        /// <summary>Drops index entries for rows that no longer exist after truncation.</summary>
        public void TrimAfter(long rowIndex)
        {
            _entries.RemoveAll(e => e.RowIndex > rowIndex);
        }

        public void Clear() => _entries.Clear();

        public int Count => _entries.Count;
    }
}
