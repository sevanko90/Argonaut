using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies byte-offset → display-row resolution over the raw segment index: row starts,
/// mid-row, the newline byte itself, soft-wrap boundaries (the byte at a forced break belongs
/// to the next row), offsets beyond the file, and the coverage wait against a still-running
/// indexer.
/// </summary>
public class RawOffsetRowResolverTests
{
    private static void WithIndex(byte[] content, int wrapWidth, Action<RawSegmentIndex> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            var index = RawSegmentIndex.StartIndexing(file, wrapWidth);
            index.IndexingTask.GetAwaiter().GetResult();
            assert(index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvesOffsetsAcrossNewlineRows()
    {
        // Rows: [0,6) incl. '\n', [6,12), [12,17) no newline.
        WithIndex("line1\nline2\nlast3"u8.ToArray(), 80, index =>
        {
            Assert.Equal(0, RawOffsetRowResolver.ResolveRowForOffset(index, 0));
            Assert.Equal(0, RawOffsetRowResolver.ResolveRowForOffset(index, 3));
            Assert.Equal(0, RawOffsetRowResolver.ResolveRowForOffset(index, 5)); // the '\n'
            Assert.Equal(1, RawOffsetRowResolver.ResolveRowForOffset(index, 6));
            Assert.Equal(2, RawOffsetRowResolver.ResolveRowForOffset(index, 16)); // final, newline-less row
        });
    }

    [Fact]
    public void ResolvesOffsetsAcrossSoftWrapBoundaries()
    {
        // 100 x's, wrap 40 → rows [0,40) [40,80) [80,100).
        var content = new byte[100];
        Array.Fill(content, (byte)'x');

        WithIndex(content, 40, index =>
        {
            Assert.Equal(0, RawOffsetRowResolver.ResolveRowForOffset(index, 39));
            Assert.Equal(1, RawOffsetRowResolver.ResolveRowForOffset(index, 40));
            Assert.Equal(2, RawOffsetRowResolver.ResolveRowForOffset(index, 99));
            Assert.Null(RawOffsetRowResolver.ResolveRowForOffset(index, 100));
        });
    }

    [Fact]
    public void OffsetBeyondFile_ReturnsNull()
    {
        WithIndex("line1\n"u8.ToArray(), 80, index =>
        {
            Assert.Null(RawOffsetRowResolver.ResolveRowForOffset(index, 100));
            Assert.Null(RawOffsetRowResolver.ResolveRowForOffset(index, -1));
        });
    }

    [Fact]
    public void EmptyFile_ReturnsNull()
    {
        WithIndex(Array.Empty<byte>(), 80, index =>
        {
            Assert.Null(RawOffsetRowResolver.ResolveRowForOffset(index, 0));
        });
    }

    [Fact]
    public async Task ResolveWhenCovered_WaitsForIndexingToReachTheOffset()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 200_000; i++)
            builder.Append("row ").Append(i).Append('\n');
        builder.Append("final");
        string content = builder.ToString();

        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        var mmap = new MMapFile(path);
        try
        {
            var index = RawSegmentIndex.StartIndexing(mmap, 512);
            long offset = content.IndexOf("final", StringComparison.Ordinal);

            int? row = await RawOffsetRowResolver.ResolveWhenCoveredAsync(index, offset, CancellationToken.None);

            Assert.Equal(200_000, row);
            await index.IndexingTask;
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
