using System.Text;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies byte-offset → line resolution: line starts, mid-line, the newline byte itself
/// (which belongs to its line's span), the final line without a trailing newline, and the
/// coverage wait against a still-running indexer.
/// </summary>
public class NdJsonOffsetLineResolverTests
{
    private static void WithIndex(string content, Action<FileOffsetIndex> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();
            assert(index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolvesOffsetsAcrossLines()
    {
        // Offsets: line0 = [0,6) incl. '\n', line1 = [6,12), line2 = [12,17) no newline.
        WithIndex("line1\nline2\nlast3", index =>
        {
            Assert.Equal(0, NdJsonOffsetLineResolver.ResolveLineForOffset(index, 0));
            Assert.Equal(0, NdJsonOffsetLineResolver.ResolveLineForOffset(index, 3));
            Assert.Equal(0, NdJsonOffsetLineResolver.ResolveLineForOffset(index, 5)); // the '\n'
            Assert.Equal(1, NdJsonOffsetLineResolver.ResolveLineForOffset(index, 6));
            Assert.Equal(2, NdJsonOffsetLineResolver.ResolveLineForOffset(index, 16)); // final, newline-less line
        });
    }

    [Fact]
    public void OffsetBeyondFile_ReturnsNull()
    {
        WithIndex("line1\n", index =>
        {
            Assert.Null(NdJsonOffsetLineResolver.ResolveLineForOffset(index, 100));
        });
    }

    [Fact]
    public void EmptyFile_ReturnsNull()
    {
        WithIndex(string.Empty, index =>
        {
            Assert.Null(NdJsonOffsetLineResolver.ResolveLineForOffset(index, 0));
        });
    }

    [Fact]
    public async Task ResolveWhenCovered_WaitsForIndexingToReachTheOffset()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 200_000; i++)
            builder.Append("{\"line\":").Append(i).Append("}\n");
        builder.Append("{\"final\":true}");
        string content = builder.ToString();

        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        var mmap = new MMapFile(path);
        try
        {
            var index = FileOffsetIndex.StartIndexing(mmap);
            long offset = content.IndexOf("final", StringComparison.Ordinal);

            int? line = await NdJsonOffsetLineResolver.ResolveWhenCoveredAsync(index, offset, CancellationToken.None);

            Assert.Equal(200_000, line);
            await index.IndexingTask;
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
