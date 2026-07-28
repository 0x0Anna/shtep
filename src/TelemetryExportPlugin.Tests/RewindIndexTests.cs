using Xunit;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class RewindIndexTests
    {
        [Fact]
        public void TryFindTruncationPoint_FindsNearestPriorEntryAtOrBeforeTarget()
        {
            var index = new RewindIndex();
            for (int i = 0; i <= 100; i += 10)
            {
                index.Add(position: i, byteOffset: i * 20, rowIndex: i, timeS: i / 10.0);
            }

            // Rewind lands at position 47 -> nearest entry at/before that is position 40.
            Assert.True(index.TryFindTruncationPoint(47, out var match));
            Assert.Equal(40, match.Position);
            Assert.Equal(800, match.ByteOffset);
            Assert.Equal(40, match.RowIndex);
        }

        [Fact]
        public void TryFindTruncationPoint_FallsBackToEarliestEntry_WhenTargetBeforeAnyEntry()
        {
            var index = new RewindIndex();
            index.Add(position: 50, byteOffset: 500, rowIndex: 25, timeS: 2.5);
            index.Add(position: 100, byteOffset: 1000, rowIndex: 50, timeS: 5.0);

            Assert.True(index.TryFindTruncationPoint(10, out var match));
            Assert.Equal(50, match.Position);
        }

        [Fact]
        public void TryFindTruncationPoint_ReturnsFalse_WhenIndexEmpty()
        {
            var index = new RewindIndex();
            Assert.False(index.TryFindTruncationPoint(10, out _));
        }

        [Fact]
        public void TrimAfter_RemovesEntriesPastTruncationRow()
        {
            var index = new RewindIndex();
            index.Add(0, 0, 0, 0);
            index.Add(10, 100, 10, 1);
            index.Add(20, 200, 20, 2);

            index.TrimAfter(10);

            Assert.Equal(2, index.Count);
        }

        [Fact]
        public void WriteRowsThenBackwardJump_TruncatesAndResumesCleanly()
        {
            // Fixture matching PLUGIN_IMPLEMENTATION_PLAN.md's rewind test description:
            // write N rows, inject a backward jump, confirm correct truncation point.
            var index = new RewindIndex();
            for (long row = 0; row < 50; row++)
            {
                index.Add(position: row * 2.0, byteOffset: row * 30, rowIndex: row, timeS: row / 100.0);
            }

            // Game rewinds the player back to position 30 (row ~15).
            Assert.True(index.TryFindTruncationPoint(30, out var match));
            Assert.Equal(15, match.RowIndex);

            index.TrimAfter(match.RowIndex);
            Assert.Equal(16, index.Count); // rows 0..15 inclusive survive
        }
    }
}
