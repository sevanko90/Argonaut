using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The diff lifecycle contract (diff plan stage 4): disposal joins the diff task before
/// either mapping is released, in every interleaving - mid-diff, mid-index on one or both
/// sides - a failed side never starts the diff but still tears down cleanly, double
/// dispose is a no-op, and cancellation mid-build leaves no partially-final container hash
/// observable. Precedent: IndexedFileSessionTests / StatusProgressHandoffTests.
/// </summary>
public class JsonDiffSessionTests
{
    private static string WriteTempJson(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    /// <summary>A file big enough that indexing it takes real time, so an immediate dispose
    /// lands mid-scan - the same technique IndexedFileSessionTests uses.</summary>
    private static string WriteLargeTempJson(int elements = 400_000)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < elements; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"i\":").Append(i).Append(",\"s\":\"value-").Append(i).Append("\"}");
        }

        sb.Append(']');
        return WriteTempJson(sb.ToString());
    }

    [Fact]
    public async Task DisposeAfterCompletion_ReleasesBothMappings()
    {
        string leftPath = WriteTempJson("""{"a":1}""");
        string rightPath = WriteTempJson("""{"a":2}""");
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            await session.Diff.IndexingTask;
            Assert.True(session.Diff.RecordCount > 0);

            var leftFile = session.Left.File;
            var rightFile = session.Right.File;
            session.Dispose();

            Assert.Throws<ObjectDisposedException>(() => leftFile.GetSpan(0, 1));
            Assert.Throws<ObjectDisposedException>(() => rightFile.GetSpan(0, 1));
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public void DisposeWhileBothSidesStillIndexing_JoinsEverythingPromptly()
    {
        string leftPath = WriteLargeTempJson();
        string rightPath = WriteLargeTempJson();
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            var diffTask = session.Diff.IndexingTask;

            // Dispose immediately - the diff task is still waiting on both indexes. It must
            // not return before the diff task has stopped touching either mapping.
            session.Dispose();

            Assert.True(diffTask.IsCompleted);
            Assert.True(session.Left.IndexingTask.IsCompleted);
            Assert.True(session.Right.IndexingTask.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => session.Left.File.GetSpan(0, 1));
            Assert.Throws<ObjectDisposedException>(() => session.Right.File.GetSpan(0, 1));
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task DisposeWhileOneSideIndexingAndOtherComplete_JoinsCleanly()
    {
        string leftPath = WriteTempJson("""{"tiny":true}""");
        string rightPath = WriteLargeTempJson();
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            await session.Left.IndexingTask; // the small side finishes fast

            session.Dispose();

            Assert.True(session.Diff.IndexingTask.IsCompleted);
            Assert.True(session.Right.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task DisposeMidDiff_DiffTaskObservedStopped()
    {
        // Both sides identical and large: indexing dominates, and the diff itself is a
        // single hash comparison - so cancel/dispose lands either during the diff's wait
        // for the indexes or during its run. Both paths must join cleanly.
        string leftPath = WriteLargeTempJson();
        string rightPath = WriteLargeTempJson();
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            await Task.Delay(50);
            session.Dispose();

            Assert.True(session.Diff.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task OneSideFailsToIndex_DiffNeverStarts_FailureAttributedToThatSide()
    {
        string leftPath = WriteTempJson("""{"ok":1}""");
        string rightPath = WriteTempJson("{\"broken\": tru"); // invalid JSON
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            await session.Diff.IndexingTask; // completes (empty) despite the side failure

            Assert.Null(session.Left.Index.Failure);
            Assert.NotNull(session.Right.Index.Failure);
            Assert.Equal(0, session.Diff.RecordCount);
            Assert.True(session.Diff.IsComplete);

            var leftFile = session.Left.File;
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => leftFile.GetSpan(0, 1));
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public void DoubleDispose_IsANoOp()
    {
        string leftPath = WriteTempJson("[1]");
        string rightPath = WriteTempJson("[2]");
        try
        {
            var session = JsonDiffSession.Start(leftPath, rightPath);
            session.Dispose();
            session.Dispose(); // view model and view detach handler both call it
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public void CancellationMidBuild_LeavesNoPartiallyFinalContainerHash()
    {
        string path = WriteLargeTempJson();
        try
        {
            var file = new MMapFile(path);
            var cts = new CancellationTokenSource();
            var index = JsonStructureIndex.StartIndexing(file, new JsonIndexOptions { ComputeContentHashes = true }, cancellationToken: cts.Token);

            cts.Cancel();
            try { index.IndexingTask.Wait(); } catch { /* cancellation observed */ }

            // If the scan was stopped mid-file, the root array never closed - its hash slot
            // must still hold the sentinel 0, never a partial value. (If the scan happened
            // to win the race and complete, the root hash is final and non-zero.)
            if (index.TokenCount > 0)
            {
                long rootHash = index.GetContentHash(0);
                if (index.GetToken(0).EndIndex < 0)
                    Assert.Equal(0, rootHash);
                else
                    Assert.NotEqual(0, rootHash);
            }

            file.Dispose();
            cts.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
