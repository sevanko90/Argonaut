using System.Buffers;
using Argonaut.Engine.Collections;

namespace Argonaut.Tests.Engine.Collections;

public class ChunkedByteWriterTests
{
    private static byte[] Concatenate(ChunkedByteWriter writer) =>
        writer.Chunks.SelectMany(chunk => chunk.Span.ToArray()).ToArray();

    [Fact]
    public void Writes_SpillIntoFreshChunks_WithoutGrowingOrCopying()
    {
        var writer = new ChunkedByteWriter(chunkBytes: 16);
        var expected = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

        foreach (byte b in expected)
            writer.Write([b]);

        Assert.Equal(expected, Concatenate(writer));
        Assert.Equal(100, writer.WrittenLength);
        Assert.All(writer.Chunks, chunk => Assert.Equal(16, chunk.Buffer.Length));
    }

    [Fact]
    public void ARequestLargerThanAChunk_GetsAChunkOfItsOwn()
    {
        var writer = new ChunkedByteWriter(chunkBytes: 16);
        writer.Write("ab"u8);

        var span = writer.GetSpan(40);
        Assert.True(span.Length >= 40);
        span[..40].Fill((byte)'x');
        writer.Advance(40);

        Assert.Equal("ab" + new string('x', 40), System.Text.Encoding.ASCII.GetString(Concatenate(writer)));
        Assert.Equal(2, writer.Chunks.Count);
    }

    [Fact]
    public void WritingAfterReadingTheChunks_NeverTouchesAChunkAlreadyHandedOut()
    {
        var writer = new ChunkedByteWriter(chunkBytes: 16);
        writer.Write("abc"u8);
        var handedOut = writer.Chunks[0];

        writer.Write("def"u8);

        Assert.Equal("abc"u8.ToArray(), handedOut.Span.ToArray());
        Assert.Equal("abcdef"u8.ToArray(), Concatenate(writer));
    }

    [Fact]
    public void Advance_PastWhatWasHandedOut_Throws()
    {
        var writer = new ChunkedByteWriter(chunkBytes: 16);
        int available = writer.GetSpan().Length;

        Assert.Throws<InvalidOperationException>(() => writer.Advance(available + 1));
    }

    [Fact]
    public void NothingWritten_HasNoChunks()
    {
        var writer = new ChunkedByteWriter();
        writer.GetSpan(10);

        Assert.Empty(writer.Chunks);
    }
}
