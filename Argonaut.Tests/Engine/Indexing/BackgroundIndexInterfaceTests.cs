using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Indexing;
using Argonaut.Engine.Indexing.Lines;
using Argonaut.Features.Json.Indexing;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests.Engine.Indexing;

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
    public void JsonSparseIndex_InterfaceMirrorsContainerCount()
    {
        WithFile("""{"a":1,"b":[true,null]}""", file =>
        {
            var index = JsonSparseIndex.StartIndexing(file, promotionBytes: 4, checkpointBytes: 2);
            IBackgroundIndex indexer = index;
            indexer.IndexingTask.GetAwaiter().GetResult();

            Assert.True(indexer.AllItemsPublished);
            Assert.Equal(2, indexer.ItemCount);
            Assert.Equal(index.Structure.ContainerCount, indexer.ItemCount);
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
    public void JsonSparseIndex_InterfaceTaskFaultsOnInvalidJson()
    {
        WithFile("{ not json", file =>
        {
            IBackgroundIndex indexer = JsonSparseIndex.StartIndexing(file);
            Assert.ThrowsAnyAsync<Exception>(() => indexer.IndexingTask).GetAwaiter().GetResult();
            Assert.True(indexer.AllItemsPublished);

            var failure = Assert.IsType<JsonSparseIndex>(indexer).Failure;
            Assert.NotNull(failure);
            Assert.NotNull(failure!.Line);
            Assert.NotNull(failure.Column);
            Assert.Equal(1, failure.ItemsIndexed); // the leading '{' was read before the failure
        });
    }

    [Fact]
    public void JsonSparseIndex_Failure_IsNullOnSuccess()
    {
        WithFile("""{"a":1}""", file =>
        {
            var index = JsonSparseIndex.StartIndexing(file);
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
