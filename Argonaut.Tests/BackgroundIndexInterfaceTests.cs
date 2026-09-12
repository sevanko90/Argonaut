using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies both indexers behave identically when seen through <see cref="IBackgroundIndex"/>:
/// the interface members mirror the indexer-specific ones, so generic consumers (the
/// completion monitor, IndexedSourceSession) can rely on either implementation.
/// </summary>
public class BackgroundIndexInterfaceTests
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
            IBackgroundIndex indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.AllItemsPublished);
            Assert.Equal(index.LineCount, indexer.ItemCount);
            Assert.Equal(3, indexer.ItemCount);
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void JsonStructureIndex_InterfaceMirrorsTokenCount()
    {
        WithFile("""{"a":1,"b":[true,null]}""", file =>
        {
            var index = JsonStructureIndex.StartIndexing(file);
            IBackgroundIndex indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.AllItemsPublished);
            Assert.Equal(index.TokenCount, indexer.ItemCount);
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void RawSegmentIndex_InterfaceCountsAnchorsWhileRowCountCountsRows()
    {
        WithFile("one\ntwo\nthree\n", file =>
        {
            var index = RawSegmentIndex.StartIndexing(file, 80);
            IBackgroundIndex indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.AllItemsPublished);
            Assert.Equal(3, index.RowCount);
            Assert.Equal(1, indexer.ItemCount); // sparse: one anchor covers the first 64 rows
            Assert.Same(index.IndexingTask, indexer.IndexingTask);
        });
    }

    [Fact]
    public void JsonStructureIndex_InterfaceTaskFaultsOnInvalidJson()
    {
        WithFile("{ not json", file =>
        {
            IBackgroundIndex indexer = JsonStructureIndex.StartIndexing(file);
            Assert.ThrowsAnyAsync<Exception>(() => indexer.IndexingTask).GetAwaiter().GetResult();
            Assert.True(indexer.AllItemsPublished);

            var failure = Assert.IsType<JsonStructureIndex>(indexer).Failure;
            Assert.NotNull(failure);
            Assert.NotNull(failure!.Line);
            Assert.NotNull(failure.Column);
            Assert.Equal(1, failure.ItemsIndexed); // the leading '{' already published as StartObject
        });
    }

    [Fact]
    public void JsonStructureIndex_Failure_IsNullOnSuccess()
    {
        WithFile("""{"a":1}""", file =>
        {
            var index = JsonStructureIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();

            Assert.Null(index.Failure);
        });
    }

    [Fact]
    public void FileOffsetIndex_Failure_IsNullOnSuccess()
    {
        WithFile("one\ntwo\nthree\n", file =>
        {
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();

            Assert.Null(index.Failure);
        });
    }

    [Fact]
    public void RawSegmentIndex_Failure_IsNullOnSuccess()
    {
        WithFile("one\ntwo\nthree\n", file =>
        {
            var index = RawSegmentIndex.StartIndexing(file, 80);
            index.IndexingTask.GetAwaiter().GetResult();

            Assert.Null(index.Failure);
        });
    }
}
