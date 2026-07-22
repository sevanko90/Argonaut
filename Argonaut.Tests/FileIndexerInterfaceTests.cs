using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies both file indexers behave identically when seen through <see cref="IFileIndexer"/>:
/// the interface members mirror the indexer-specific ones, so generic consumers (the
/// completion monitor, IndexedFileSession) can rely on either implementation.
/// </summary>
public class FileIndexerInterfaceTests
{
    private static void WithFile(string content, Action<MMapFile> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            assert(file);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileOffsetIndex_InterfaceMirrorsLineCount()
    {
        WithFile("one\ntwo\nthree\n", file =>
        {
            var index = FileOffsetIndex.StartIndexing(file);
            IFileIndexer indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.IsComplete);
            Assert.Equal(index.LineCount, indexer.ItemCount);
            Assert.Equal(3, indexer.ItemCount);
            Assert.Equal("lines", indexer.ItemNoun);
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void JsonStructureIndex_InterfaceMirrorsTokenCount()
    {
        WithFile("""{"a":1,"b":[true,null]}""", file =>
        {
            var index = JsonStructureIndex.StartIndexing(file);
            IFileIndexer indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.IsComplete);
            Assert.Equal(index.TokenCount, indexer.ItemCount);
            Assert.Equal("tokens", indexer.ItemNoun);
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void RawSegmentIndex_InterfaceCountsAnchorsWhileRowCountCountsRows()
    {
        WithFile("one\ntwo\nthree\n", file =>
        {
            var index = RawSegmentIndex.StartIndexing(file, 80);
            IFileIndexer indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.IsComplete);
            Assert.Equal(3, index.RowCount);
            Assert.Equal(1, indexer.ItemCount); // sparse: one anchor covers the first 64 rows
            Assert.Equal("anchors", indexer.ItemNoun);
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void JsonStructureIndex_InterfaceTaskFaultsOnInvalidJson()
    {
        WithFile("{ not json", file =>
        {
            IFileIndexer indexer = JsonStructureIndex.StartIndexing(file);
            Assert.ThrowsAnyAsync<Exception>(() => indexer.IndexingTask).GetAwaiter().GetResult();
            Assert.True(indexer.IsComplete);
        });
    }
}
