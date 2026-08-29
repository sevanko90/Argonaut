using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Diff;
using Argonaut.Features.NdJson;
using Argonaut.Features.Raw;
using Argonaut.Features.Search;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// End-to-end coverage of document teardown while background work is live. Two scenarios per
/// document type (JSON, CSV, NDJSON, Raw, JSON diff): disposing while the document's own
/// background indexing is still running, and disposing while a search scan over the same file
/// is wedged mid-chunk - the "view's own detach handler tears the document down without the
/// shell's FindController stopping the search first" case (window close mid-search).
///
/// The search scenario asserts the property scan-owned mappings buy, which
/// is the OPPOSITE of what it asserted while search borrowed the document's mapping: Dispose no
/// longer waits for a search at all. A scan owns its own chunk mappings, so a document can be
/// released out from under a wedged scan, and the scan carries on reading afterwards without
/// touching anything the document freed. (Before, the same test proved Dispose blocked until
/// the scan let go - correct then, and exactly the coupling that has now been removed.)
///
/// Every assertion is still ultimately one thing: <see cref="IDisposable.Dispose"/> returns
/// promptly and nothing throws or crashes. Content sizes are chosen so LoadAsync's initial-batch
/// wait returns while a real background scan is still provably running, mirroring
/// JsonDiffSessionTests' "immediate dispose lands mid-scan" technique.
/// </summary>
public class DocumentDisposalLifecycleTests
{
    private const int LargeElementCount = 500_000;

    private static string WriteTemp(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string WriteLargeJson()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < LargeElementCount; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"i\":").Append(i).Append(",\"s\":\"value-").Append(i).Append("\"}");
        }
        sb.Append(']');
        return WriteTemp(sb.ToString());
    }

    private static string WriteLargeCsv()
    {
        var sb = new StringBuilder("a,b,c\n");
        for (int i = 0; i < LargeElementCount; i++)
            sb.Append(i).Append(",value-").Append(i).Append(",z\n");
        return WriteTemp(sb.ToString());
    }

    private static string WriteLargeNdJson()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LargeElementCount; i++)
            sb.Append("{\"i\":").Append(i).Append(",\"s\":\"value-").Append(i).Append("\"}\n");
        return WriteTemp(sb.ToString());
    }

    private static string WriteLargeRaw()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < LargeElementCount; i++)
            sb.Append("row ").Append(i).Append('\n');
        return WriteTemp(sb.ToString());
    }

    /// <summary>Runs <paramref name="dispose"/> on a background thread and asserts it completes
    /// within <paramref name="timeoutMs"/> - the "does not hang" half of every test here. Any
    /// exception thrown by Dispose itself (the "does not crash" half) propagates as a normal
    /// assertion failure via the awaited task.</summary>
    private static async Task DisposeWithTimeoutAsync(Action dispose, int timeoutMs = 10_000)
    {
        var task = Task.Run(dispose);
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(ReferenceEquals(finished, task), "Dispose() did not return within the timeout - it hung.");
        await task; // rethrows if Dispose faulted
    }

    /// <summary>Blocks a search scan inside its first window until released - same technique as
    /// FileSearchSessionTests.BlockingMatcher, used here to force a deterministic interleaving:
    /// the scan is provably still holding a span over the mapping when Dispose is called.</summary>
    private sealed class BlockingMatcher : ISearchMatcher
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public int ChunkOverlap => 0;

        public bool TryFindNext(ReadOnlySpan<byte> chunk, int from, out int matchIndex, out int matchLength)
        {
            Entered.Set();
            Release.Wait();
            matchIndex = -1;
            matchLength = 0;
            return false;
        }
    }

    /// <summary>
    /// Starts a search scan over <paramref name="path"/> - the same way FindController does,
    /// from a ScanTarget rather than the document's mapping - wedges it mid-chunk, then
    /// disposes the document and asserts Dispose returns WHILE the scan is still wedged.
    ///
    /// That ordering is the whole point. The matcher is released only after Dispose has already
    /// returned, so nothing about the document's teardown can have depended on the scan
    /// stopping: if Dispose still joined search work, this would deadlock. Afterwards the scan
    /// is released and must complete cleanly and unfaulted - it is reading chunk mappings it
    /// opened itself, so the document releasing its own mapping is none of its business.
    /// </summary>
    private static async Task DisposeDuringActiveSearchAsync(string path, Action dispose)
    {
        var matcher = new BlockingMatcher();
        var session = FileSearchSession.Start(new ScanTarget(path), matcher);

        matcher.Entered.Wait();

        var disposeTask = Task.Run(dispose);

        var finished = await Task.WhenAny(disposeTask, Task.Delay(10_000));
        Assert.True(ReferenceEquals(finished, disposeTask),
            "Dispose() did not return while a search scan was wedged - document teardown is still waiting on search.");
        await disposeTask; // rethrows if Dispose faulted

        // Only now let the scan go: it must finish on its own terms, reading a mapping the
        // just-disposed document never owned.
        Assert.False(session.ScanTask.IsCompleted);
        session.RequestStop();
        matcher.Release.Set();

        await session.ScanTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(session.OpenFailure);
    }

    [Fact]
    public async Task Json_CloseDuringIndexing_DoesNotHangOrCrash()
    {
        string path = WriteLargeJson();
        try
        {
            var vm = new JsonViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted); // sanity: indexing genuinely still running

            await DisposeWithTimeoutAsync(vm.Dispose);
            Assert.True(vm.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Json_CloseDuringIndexingWithActiveSearch_DoesNotHangOrCrash()
    {
        string path = WriteLargeJson();
        try
        {
            var vm = new JsonViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeDuringActiveSearchAsync(path, vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Csv_CloseDuringIndexing_DoesNotHangOrCrash()
    {
        string path = WriteLargeCsv();
        try
        {
            var vm = new CsvViewModel();
            await vm.LoadAsync(path, (byte)',');
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeWithTimeoutAsync(vm.Dispose);
            Assert.True(vm.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Csv_CloseDuringIndexingWithActiveSearch_DoesNotHangOrCrash()
    {
        string path = WriteLargeCsv();
        try
        {
            var vm = new CsvViewModel();
            await vm.LoadAsync(path, (byte)',');
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeDuringActiveSearchAsync(path, vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NdJson_CloseDuringIndexing_DoesNotHangOrCrash()
    {
        string path = WriteLargeNdJson();
        try
        {
            var vm = new NdJsonViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeWithTimeoutAsync(vm.Dispose);
            Assert.True(vm.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NdJson_CloseDuringIndexingWithActiveSearch_DoesNotHangOrCrash()
    {
        string path = WriteLargeNdJson();
        try
        {
            var vm = new NdJsonViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeDuringActiveSearchAsync(path, vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>NDJSON's extra wrinkle: a line selected into the nested per-line JsonViewModel
    /// (its own session/mapping) must also tear down cleanly as part of the parent's dispose -
    /// DisposeCore's job post-Stage-2.</summary>
    [Fact]
    public async Task NdJson_CloseWithSelectedLine_DoesNotHangOrCrash()
    {
        string path = WriteLargeNdJson();
        try
        {
            var vm = new NdJsonViewModel();
            await vm.LoadAsync(path);
            vm.LoadSelectedLine(0);
            await Task.Delay(20); // let the nested JsonViewModel.LoadAsync start

            await DisposeWithTimeoutAsync(vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Raw_CloseDuringIndexing_DoesNotHangOrCrash()
    {
        string path = WriteLargeRaw();
        try
        {
            var vm = new RawViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeWithTimeoutAsync(vm.Dispose);
            Assert.True(vm.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Raw_CloseDuringIndexingWithActiveSearch_DoesNotHangOrCrash()
    {
        string path = WriteLargeRaw();
        try
        {
            var vm = new RawViewModel();
            await vm.LoadAsync(path);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeDuringActiveSearchAsync(path, vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Raw's extra wrinkle: closing while a wrap-width restart (its own re-index, with
    /// its own linked CTS) is in flight, on top of the same search-scope contract as the other
    /// four types.</summary>
    [Fact]
    public async Task Raw_CloseDuringWrapWidthRestartWithActiveSearch_DoesNotHangOrCrash()
    {
        string path = WriteLargeRaw();
        try
        {
            var vm = new RawViewModel();
            await vm.LoadAsync(path);
            await vm.IndexingTask;

            vm.SetWrapWidth(vm.WrapWidth == 80 ? 160 : 80);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeDuringActiveSearchAsync(path, vm.Dispose);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Diff_CloseDuringIndexing_DoesNotHangOrCrash()
    {
        string leftPath = WriteLargeJson();
        string rightPath = WriteLargeJson();
        try
        {
            var vm = new JsonDiffViewModel();
            await vm.LoadAsync(leftPath, rightPath);
            Assert.False(vm.IndexingTask.IsCompleted);

            await DisposeWithTimeoutAsync(vm.Dispose);
            Assert.True(vm.IndexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task Diff_CloseDuringIndexingWithActiveSearchOnLeftSide_DoesNotHangOrCrash()
    {
        string leftPath = WriteLargeJson();
        string rightPath = WriteLargeJson();
        try
        {
            var vm = new JsonDiffViewModel();
            await vm.LoadAsync(leftPath, rightPath);
            Assert.False(vm.IndexingTask.IsCompleted);

            var navigator = (JsonDiffSearchNavigator)vm.CreateSearchNavigator()!;
            Assert.Equal(leftPath, navigator.ScanTargets[0].Path);

            await DisposeDuringActiveSearchAsync(leftPath, vm.Dispose);
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    [Fact]
    public async Task Diff_CloseDuringIndexingWithActiveSearchOnRightSide_DoesNotHangOrCrash()
    {
        string leftPath = WriteLargeJson();
        string rightPath = WriteLargeJson();
        try
        {
            var vm = new JsonDiffViewModel();
            await vm.LoadAsync(leftPath, rightPath);
            Assert.False(vm.IndexingTask.IsCompleted);

            var navigator = (JsonDiffSearchNavigator)vm.CreateSearchNavigator()!;
            Assert.Equal(rightPath, navigator.ScanTargets[1].Path);

            await DisposeDuringActiveSearchAsync(rightPath, vm.Dispose);
        }
        finally
        {
            File.Delete(leftPath);
            File.Delete(rightPath);
        }
    }

    /// <summary>Double-dispose (shell path + view's own detach handler, per
    /// IDocumentViewModel's contract) must stay a no-op even when the first dispose landed
    /// mid-scan for every document type - not just the trivial already-idle case the existing
    /// per-type DoubleDispose tests cover.</summary>
    [Fact]
    public async Task AllDocumentTypes_DoubleDisposeAfterMidScanClose_IsANoOp()
    {
        {
            string path = WriteLargeJson();
            try
            {
                var vm = new JsonViewModel();
                await vm.LoadAsync(path);
                await DisposeWithTimeoutAsync(vm.Dispose);
                vm.Dispose();
            }
            finally { File.Delete(path); }
        }
        {
            string path = WriteLargeCsv();
            try
            {
                var vm = new CsvViewModel();
                await vm.LoadAsync(path, (byte)',');
                await DisposeWithTimeoutAsync(vm.Dispose);
                vm.Dispose();
            }
            finally { File.Delete(path); }
        }
        {
            string path = WriteLargeNdJson();
            try
            {
                var vm = new NdJsonViewModel();
                await vm.LoadAsync(path);
                await DisposeWithTimeoutAsync(vm.Dispose);
                vm.Dispose();
            }
            finally { File.Delete(path); }
        }
        {
            string path = WriteLargeRaw();
            try
            {
                var vm = new RawViewModel();
                await vm.LoadAsync(path);
                await DisposeWithTimeoutAsync(vm.Dispose);
                vm.Dispose();
            }
            finally { File.Delete(path); }
        }
        {
            string leftPath = WriteLargeJson();
            string rightPath = WriteLargeJson();
            try
            {
                var vm = new JsonDiffViewModel();
                await vm.LoadAsync(leftPath, rightPath);
                await DisposeWithTimeoutAsync(vm.Dispose);
                vm.Dispose();
            }
            finally
            {
                File.Delete(leftPath);
                File.Delete(rightPath);
            }
        }
    }
}
