using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The piece table is differential-tested against a <see cref="List{T}"/> of bytes mutated by the
/// same edit script - the same oracle shape <see cref="RawSegmentIndexTests"/> uses for the
/// segmentation rules, for the same reason: an independent implementation disagreeing is the only
/// cheap way to catch an error in the one that is clever.
/// </summary>
public class RawPieceTableTests
{
    private static byte[] Bytes(string text) => System.Text.Encoding.UTF8.GetBytes(text);

    /// <summary>Reads the whole document back through the public surface, as a reader would.</summary>
    private static byte[] ReadAll(IByteSource source)
    {
        var destination = new byte[source.AvailableLength];
        int copied = source.CopyTo(0, destination);
        Assert.Equal(destination.Length, copied);
        return destination;
    }

    private static RawPieceTable TableOver(string text) => new(new ArrayByteSource(Bytes(text)));

    [Fact]
    public void UneditedTable_ReadsExactlyTheOriginal()
    {
        var table = TableOver("hello world");

        Assert.True(table.IsUnedited);
        Assert.Equal(11, table.AvailableLength);
        Assert.Equal(Bytes("hello world"), ReadAll(table));
    }

    [Fact]
    public void EmptyOriginal_HasNoPiecesAndReadsEmpty()
    {
        var table = TableOver(string.Empty);

        Assert.Equal(0, table.AvailableLength);
        Assert.Equal(0, table.PieceCount);
        Assert.Empty(ReadAll(table));
    }

    [Theory]
    [InlineData(0, "XXhello world")]
    [InlineData(5, "helloXX world")]
    [InlineData(11, "hello worldXX")]
    public void Insert_PlacesBytesAtTheOffset(int offset, string expected)
    {
        var table = TableOver("hello world");

        var extent = table.Insert(offset, Bytes("XX"));

        Assert.Equal(Bytes(expected), ReadAll(table));
        Assert.Equal(new RawEditExtent(offset, 0, 2), extent);
        Assert.Equal(2, extent.ByteDelta);
        Assert.False(table.IsUnedited);
    }

    [Fact]
    public void Delete_RemovesTheRange()
    {
        var table = TableOver("hello world");

        var extent = table.Delete(5, 6);

        Assert.Equal(Bytes("hello"), ReadAll(table));
        Assert.Equal(new RawEditExtent(5, 6, 0), extent);
        Assert.Equal(-6, extent.ByteDelta);
    }

    [Fact]
    public void Delete_SpanningSeveralPieces_RemovesAllOfThem()
    {
        var table = TableOver("abcdef");
        table.Insert(2, Bytes("111"));   // ab111cdef
        table.Insert(7, Bytes("222"));   // ab111cd222ef
        Assert.Equal(Bytes("ab111cd222ef"), ReadAll(table));

        table.Delete(1, 9);              // a + ef

        Assert.Equal(Bytes("aef"), ReadAll(table));
    }

    [Fact]
    public void Replace_IsOneEditCoveringBothSides()
    {
        var table = TableOver("hello world");

        var extent = table.Replace(6, 5, Bytes("there"));

        Assert.Equal(Bytes("hello there"), ReadAll(table));
        Assert.Equal(new RawEditExtent(6, 5, 5), extent);
        Assert.Equal(0, extent.ByteDelta);
    }

    [Fact]
    public void EmptyEdits_ChangeNothing()
    {
        var table = TableOver("abc");

        Assert.Equal(new RawEditExtent(1, 0, 0), table.Insert(1, ReadOnlySpan<byte>.Empty));
        Assert.Equal(new RawEditExtent(1, 0, 0), table.Delete(1, 0));
        Assert.Equal(Bytes("abc"), ReadAll(table));
    }

    [Fact]
    public void OffsetsOutsideTheDocument_Throw()
    {
        var table = TableOver("abc");

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Insert(-1, Bytes("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Insert(4, Bytes("x")));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Delete(2, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Delete(-1, 1));
    }

    [Fact]
    public void GetContiguousSpan_TruncatesAtAPieceBoundaryButCopyToStitches()
    {
        var table = TableOver("abcdef");
        table.Insert(3, Bytes("XY")); // abc | XY | def - three pieces

        // A read spanning the whole document is served in pieces...
        var firstSpan = table.GetContiguousSpan(0, (int)table.AvailableLength);
        Assert.Equal(Bytes("abc"), firstSpan.ToArray());

        // ...while CopyTo crosses the boundaries for callers that need one buffer.
        Assert.Equal(Bytes("abcXYdef"), ReadAll(table));
    }

    [Fact]
    public void GetContiguousSpan_OutsideTheDocument_IsEmpty()
    {
        var table = TableOver("abc");

        Assert.True(table.GetContiguousSpan(-1, 1).IsEmpty);
        Assert.True(table.GetContiguousSpan(3, 1).IsEmpty);
        Assert.True(table.GetContiguousSpan(0, 0).IsEmpty);
    }

    [Fact]
    public void PasteLargerThanAScratchChunk_StaysOnePiece()
    {
        var table = TableOver("ab");
        var paste = new byte[200 * 1024];
        Random.Shared.NextBytes(paste);

        table.Insert(1, paste);

        // a | paste | b
        Assert.Equal(3, table.PieceCount);
        var read = ReadAll(table);
        Assert.Equal((byte)'a', read[0]);
        Assert.Equal(paste, read[1..^1]);
        Assert.Equal((byte)'b', read[^1]);
    }

    [Fact]
    public void RepointOriginal_KeepsEditsReadableOverEquivalentBytes()
    {
        var table = TableOver("hello world");
        table.Insert(5, Bytes(" big"));
        Assert.Equal(Bytes("hello big world"), ReadAll(table));

        // What a save does when the rename fails: the mapping is gone, so re-open and carry on.
        table.RepointOriginal(new ArrayByteSource(Bytes("hello world")));

        Assert.Equal(Bytes("hello big world"), ReadAll(table));
    }

    [Fact]
    public void RepointOriginal_RejectsADifferentLength()
    {
        var table = TableOver("hello world");

        Assert.Throws<ArgumentException>(() => table.RepointOriginal(new ArrayByteSource(Bytes("shorter"))));
    }

    [Fact]
    public void SnapshotAndRestore_RoundTripsAnEdit()
    {
        var table = TableOver("hello world");
        var before = table.Snapshot();

        table.Insert(5, Bytes(" big"));
        Assert.Equal(Bytes("hello big world"), ReadAll(table));

        table.Restore(before);

        Assert.Equal(Bytes("hello world"), ReadAll(table));
        Assert.Equal(11, table.AvailableLength);
    }

    [Fact]
    public void SnapshotAndRestore_ReplaysForwardAgainForRedo()
    {
        var table = TableOver("hello world");
        var before = table.Snapshot();
        table.Insert(5, Bytes(" big"));
        var after = table.Snapshot();

        table.Restore(before);
        table.Restore(after);

        // Scratch is append-only, so the redone edit's bytes are still there to point at.
        Assert.Equal(Bytes("hello big world"), ReadAll(table));
    }

    /// <summary>
    /// The core of this file: a long randomised edit script applied to both the table and a
    /// <see cref="List{T}"/> oracle, with the whole document compared after every single edit, so
    /// a divergence is reported at the edit that caused it rather than at the end.
    /// </summary>
    [Theory]
    [InlineData(12345)]
    [InlineData(67890)]
    [InlineData(1)]
    public void RandomEditScript_MatchesAByteListOracle(int seed)
    {
        var random = new Random(seed);
        var seedBytes = new byte[4096];
        random.NextBytes(seedBytes);

        var table = new RawPieceTable(new ArrayByteSource(seedBytes));
        var oracle = new List<byte>(seedBytes);

        for (int step = 0; step < 400; step++)
        {
            long length = table.AvailableLength;
            Assert.Equal(oracle.Count, length);

            bool deleting = length > 0 && random.Next(100) < 40;
            if (deleting)
            {
                int offset = random.Next((int)length);
                int count = random.Next(1, Math.Min(64, (int)length - offset + 1));
                table.Delete(offset, count);
                oracle.RemoveRange(offset, count);
            }
            else
            {
                int offset = random.Next((int)length + 1);
                var payload = new byte[random.Next(1, 40)];
                random.NextBytes(payload);
                table.Insert(offset, payload);
                oracle.InsertRange(offset, payload);
            }

            Assert.Equal(oracle.Count, table.AvailableLength);
            Assert.Equal(oracle.ToArray(), ReadAll(table));
        }
    }

    /// <summary>
    /// Every offset must resolve to the right byte through the binary search, not just a whole
    /// read from zero - a piece-boundary error is invisible to a sequential read that happens to
    /// stitch correctly.
    /// </summary>
    [Fact]
    public void EveryOffset_ResolvesIndependently()
    {
        var table = TableOver("abcdefghij");
        table.Insert(3, Bytes("XY"));
        table.Insert(7, Bytes("Z"));
        table.Delete(1, 2);

        var whole = ReadAll(table);
        for (int offset = 0; offset < whole.Length; offset++)
        {
            var span = table.GetContiguousSpan(offset, 1);
            Assert.False(span.IsEmpty, $"offset {offset} resolved to nothing");
            Assert.Equal(whole[offset], span[0]);
        }
    }
}
