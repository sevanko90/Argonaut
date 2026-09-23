using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Progress;
using Argonaut.Features.Raw.Editing;

namespace Argonaut.Tests;

/// <summary>
/// The two byte-layer pieces a save stands on: the source that can let go of its mapping for the
/// swap and take an equivalent one back, and the writer that copies a whole document out.
/// </summary>
public sealed class RemappableByteSourceTests
{
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private sealed class ReleaseCountingSource(byte[] bytes) : IByteSource, IDisposable
    {
        private readonly MemoryByteSource inner = new(bytes);

        public int Releases { get; private set; }

        public long AvailableLength => inner.AvailableLength;

        public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength) => inner.GetContiguousSpan(offset, maxLength);

        public int CopyTo(long offset, Span<byte> destination) => inner.CopyTo(offset, destination);

        public void Dispose() => Releases++;
    }

    [Fact]
    public void ReadsThroughTheSourceUnderneath()
    {
        var source = new RemappableByteSource(new MemoryByteSource(Bytes("hello")));

        Assert.Equal(5, source.AvailableLength);
        Assert.Equal("hello", Encoding.UTF8.GetString(source.GetContiguousSpan(0, 5)));
    }

    [Fact]
    public void Unmap_ReleasesTheSource_AndReadsFailUntilRemapped()
    {
        var mapped = new ReleaseCountingSource(Bytes("hello"));
        var source = new RemappableByteSource(mapped);

        source.Unmap();

        Assert.Equal(1, mapped.Releases);
        Assert.False(source.IsMapped);
        Assert.Throws<ObjectDisposedException>(() => source.GetContiguousSpan(0, 1).Length);

        source.Remap(new MemoryByteSource(Bytes("hello")), 5);

        Assert.Equal((byte)'h', source.ByteAt(0));
    }

    [Fact]
    public void Remap_RefusesBytesOfADifferentLength_AndReleasesThem()
    {
        var source = new RemappableByteSource(new MemoryByteSource(Bytes("hello")));
        source.Unmap();
        var shorter = new ReleaseCountingSource(Bytes("hi"));

        Assert.Throws<InvalidOperationException>(() => source.Remap(shorter, 5));

        Assert.Equal(1, shorter.Releases);
        Assert.False(source.IsMapped);
    }

    [Fact]
    public void Remap_WhileStillMapped_IsRefused()
    {
        var source = new RemappableByteSource(new MemoryByteSource(Bytes("hello")));

        Assert.Throws<InvalidOperationException>(() => source.Remap(new MemoryByteSource(Bytes("hello")), 5));
    }

    [Fact]
    public void Release_IsIdempotent()
    {
        var mapped = new ReleaseCountingSource(Bytes("hello"));
        var source = new RemappableByteSource(mapped);

        source.Release();
        source.Release();

        Assert.Equal(1, mapped.Releases);
    }

    [Fact]
    public void AnEditedPieceTable_KeepsReadingAcrossAnUnmapAndRemap()
    {
        var source = new RemappableByteSource(new MemoryByteSource(Bytes("hello world")));
        var table = new RawPieceTable(source);
        table.Insert(5, Bytes(","));

        source.Unmap();
        source.Remap(new MemoryByteSource(Bytes("hello world")), 11);

        var read = new byte[table.AvailableLength];
        table.CopyTo(0, read);
        Assert.Equal("hello, world", Encoding.UTF8.GetString(read));
    }

    [Fact]
    public void WriteTo_WritesAPieceTableWhole_AcrossItsPieces()
    {
        var table = new RawPieceTable(new MemoryByteSource(Bytes("hello world")));
        table.Insert(5, Bytes(","));
        table.Insert(12, Bytes("!"));
        table.Delete(0, 1);
        table.Insert(0, Bytes("J"));

        using var written = new MemoryStream();
        long count = table.WriteTo(written, progress: null, CancellationToken.None);

        Assert.Equal("Jello, world!", Encoding.UTF8.GetString(written.ToArray()));
        Assert.Equal(13, count);
    }

    [Fact]
    public void WriteTo_ReportsProgressUpToTheWholeLength()
    {
        var content = new byte[ByteSourceReading.WriteChunkBytes + 100];
        var progress = new RecordingProgress();

        using var written = new MemoryStream();
        new MemoryByteSource(content).WriteTo(written, progress, CancellationToken.None);

        Assert.Equal(content.Length, written.Length);
        Assert.Equal(content.Length, progress.Last);
    }

    [Fact]
    public void WriteTo_ThrowsRatherThanWritingShort()
    {
        using var written = new MemoryStream();

        Assert.Throws<IOException>(() => new EndsEarlySource().WriteTo(written, null, CancellationToken.None));
    }

    [Fact]
    public void WriteTo_RefusesASourceStillGrowing()
    {
        var growing = new GrowingByteSource(Bytes("partial document"));

        using var written = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => growing.WriteTo(written, null, CancellationToken.None));
    }

    [Fact]
    public void WriteTo_StopsWhenAsked()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        using var written = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() =>
            new MemoryByteSource(Bytes("hello")).WriteTo(written, null, stopped.Token));
    }

    /// <summary>Claims ten bytes and serves four - the shape of a source whose backing went away
    /// part way through a save.</summary>
    private sealed class EndsEarlySource : IByteSource
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes("four");

        public long AvailableLength => 10;

        public ReadOnlySpan<byte> GetContiguousSpan(long offset, int maxLength) =>
            offset >= bytes.Length ? ReadOnlySpan<byte>.Empty : bytes.AsSpan((int)offset, Math.Min(maxLength, bytes.Length - (int)offset));

        public int CopyTo(long offset, Span<byte> destination) => throw new NotSupportedException();
    }

    private sealed class RecordingProgress : IProgressReporter
    {
        public long Last { get; private set; }

        public void Report(string message, long? current = null, long? max = null) => Last = current ?? Last;
    }
}
