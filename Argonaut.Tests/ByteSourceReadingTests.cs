using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The read idioms every <see cref="IByteSource"/> consumer uses in place of the old
/// <c>MMapFile.GetSpan</c>. The contract that matters is the pair: over a single-buffer source
/// <see cref="ByteSourceReading.RequireContiguous"/> is the zero-copy whole-range read it
/// replaced, and over a split one it refuses loudly rather than returning a short span that a
/// caller would silently treat as the whole range.
/// </summary>
public class ByteSourceReadingTests
{
    private static ArrayByteSource Source(string text) => new(Encoding.UTF8.GetBytes(text));

    /// <summary>A piece table with an edit in the middle: the only source in the codebase that
    /// can serve a range in two parts, so the only one that exercises the split contract.</summary>
    private static RawPieceTable Split()
    {
        var table = new RawPieceTable(Source("hello world"));
        table.Insert(5, ","u8);
        return table;
    }

    [Fact]
    public void RequireContiguous_OverASingleBuffer_ReturnsTheWholeRange()
    {
        var source = Source("hello world");

        Assert.True(source.RequireContiguous(0, 11).SequenceEqual("hello world"u8));
        Assert.True(source.RequireContiguous(6, 5).SequenceEqual("world"u8));
    }

    [Fact]
    public void RequireContiguous_ZeroLength_IsEmptyEvenAtTheEnd()
    {
        var source = Source("hello");

        Assert.True(source.RequireContiguous(0, 0).IsEmpty);
        Assert.True(source.RequireContiguous(5, 0).IsEmpty);
    }

    [Theory]
    [InlineData(0, 12)]
    [InlineData(11, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    public void RequireContiguous_OutsideTheData_Throws(long offset, int length)
    {
        var source = Source("hello world");

        Assert.Throws<ArgumentOutOfRangeException>(() => source.RequireContiguous(offset, length));
    }

    [Fact]
    public void RequireContiguous_ARangeSplitAcrossPieces_ThrowsRatherThanReturningPartOfIt()
    {
        var table = Split();

        // The bytes are all present - CopyTo gathers them - but no single span can describe them,
        // so the whole-range read has to fail instead of quietly yielding the first piece.
        Assert.Equal(12, table.AvailableLength);
        Assert.Throws<NotSupportedException>(() => table.RequireContiguous(0, 12));

        var destination = new byte[12];
        Assert.Equal(12, table.CopyTo(0, destination));
        Assert.Equal("hello, world", Encoding.UTF8.GetString(destination));
    }

    [Fact]
    public void RequireContiguous_ARangeInsideOnePiece_IsStillServedWhole()
    {
        var table = Split();

        Assert.True(table.RequireContiguous(0, 5).SequenceEqual("hello"u8));
        Assert.True(table.RequireContiguous(6, 6).SequenceEqual(" world"u8));
    }

    [Fact]
    public void ByteAt_ReadsOneByte_AndRejectsAnOffsetOutsideTheData()
    {
        var source = Source("hello");

        Assert.Equal((byte)'h', source.ByteAt(0));
        Assert.Equal((byte)'o', source.ByteAt(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ByteAt(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ByteAt(-1));
    }

    [Fact]
    public void ByteAt_NeverStraddles_SoItWorksEitherSideOfAPieceBoundary()
    {
        var table = Split();

        Assert.Equal((byte)'o', table.ByteAt(4));
        Assert.Equal((byte)',', table.ByteAt(5));
        Assert.Equal((byte)' ', table.ByteAt(6));
    }

    [Fact]
    public void GetUtf8String_DecodesTheRange()
    {
        var source = Source("{\"a\":\"café\"}");

        Assert.Equal("café", source.GetUtf8String(6, Encoding.UTF8.GetByteCount("café")));
    }

    [Fact]
    public void Release_UnmapsAMapping()
    {
        string path = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(path, "hello");
            IByteSource source = new MMapFile(path);

            source.Release();

            Assert.Throws<ObjectDisposedException>(() => source.GetContiguousSpan(0, 1));
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void Release_OverASourceHoldingNothing_IsANoOpRatherThanAnError()
    {
        IByteSource source = Source("hello");

        source.Release();

        Assert.True(source.RequireContiguous(0, 5).SequenceEqual("hello"u8));
    }
}
