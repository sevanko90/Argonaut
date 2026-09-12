using System.Reflection;
using System.Text;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the session's ownership contract: Dispose joins the indexing task and every
/// registered dependent task before releasing the mapping, ownership of the file transfers
/// on Start (even when the factory throws), and Cancel/Dispose are idempotent.
/// </summary>
public class IndexedFileSessionTests
{
    /// <summary>
    /// Controllable indexer stub: its IndexingTask completes only when the session's token
    /// is cancelled, mimicking a cooperative background scan.
    /// </summary>
    private sealed class StubIndexer : IFileIndexer
    {
        public Task IndexingTask { get; init; } = Task.CompletedTask;
        public bool IsComplete => IndexingTask.IsCompleted;
        public int ItemCount => 0;
        public IndexFailure? Failure => null;
    }

    private static string WriteTempFile(string content)
    {
        string path = Path.GetTempFileName();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
        return path;
    }

    [Fact]
    public void StartAndDispose_JoinsIndexingTask()
    {
        string path = WriteTempFile(string.Join('\n', Enumerable.Range(0, 10_000)));
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);
            var indexingTask = session.IndexingTask;

            // Dispose immediately - possibly mid-scan. It must not return before the
            // indexing task has stopped touching the mapping.
            session.Dispose();

            Assert.True(indexingTask.IsCompleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dispose_JoinsDependentTasksAfterCancellingToken()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);

            bool dependentRan = false;
            var tokenCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.TearingDown.Register(() => tokenCancelled.SetResult());

            // Completes only after the session cancels its token, then flips the flag -
            // Dispose must have waited for that flip before returning.
            session.RegisterDependentTask(Task.Run(async () =>
            {
                await tokenCancelled.Task;
                dependentRan = true;
            }));

            session.Dispose();

            Assert.True(dependentRan);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);
            session.Dispose();
            session.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Regression: the nested per-line JsonViewModel is disposed from two independent
    /// paths (its owning NdJsonViewModel, and its JsonView's own detach handler when the
    /// visual tree tears down) - a Cancel() arriving after Dispose() already released the
    /// CancellationTokenSource must not throw ObjectDisposedException.
    /// </summary>
    [Fact]
    public void CancelAfterDispose_DoesNotThrow()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);
            session.Dispose();
            session.RequestStop();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Start_DisposesFileWhenFactoryThrows()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var file = new MMapFile(path);

            Assert.Throws<InvalidOperationException>(() =>
                IndexedFileSession<StubIndexer>.Start(file,
                    (_, _, _) => throw new InvalidOperationException("factory failed")));

            // MMapFile exposes no disposed-state API (by design - nothing should care at
            // runtime), so this ownership test reads the private flag directly.
            var disposedField = typeof(MMapFile).GetField("disposed", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.True((bool)disposedField.GetValue(file)!);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Cancel_CancelsToken()
    {
        string path = WriteTempFile("line\n");
        try
        {
            using var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);

            Assert.False(session.TearingDown.IsCancellationRequested);
            session.RequestStop();
            session.RequestStop();
            Assert.True(session.TearingDown.IsCancellationRequested);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RegisterDependentTask_AfterDispose_IsNoOp()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);
            session.Dispose();

            // Must neither throw nor block a later (idempotent) Dispose.
            session.RegisterDependentTask(Task.Delay(Timeout.Infinite));
            session.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Reads the private dependentTasks list's Count via reflection - same technique
    /// this file already uses for MMapFile.disposed - to observe RegisterDependentTask's
    /// pruning, which has no other externally observable signal.</summary>
    private static int DependentTaskCount(IndexedFileSession<FileOffsetIndex> session)
    {
        var field = typeof(IndexedFileSession<FileOffsetIndex>).GetField("dependentTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var list = (System.Collections.IList)field.GetValue(session)!;
        return list.Count;
    }

    /// <summary>
    /// A long session with many term changes (each registering a search scan) must not
    /// accumulate completed Task references forever.
    /// Registering a new (live) task prunes every already-completed entry first.
    /// </summary>
    [Fact]
    public void RegisterDependentTask_PrunesCompletedEntriesOnEachRegister()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);

            for (int i = 0; i < 10; i++)
                session.RegisterDependentTask(Task.CompletedTask);

            Assert.Equal(1, DependentTaskCount(session)); // each register prunes before adding itself

            var stillRunning = new TaskCompletionSource();
            session.RegisterDependentTask(stillRunning.Task);
            Assert.Equal(1, DependentTaskCount(session)); // the prior completed entry was pruned first

            for (int i = 0; i < 10; i++)
                session.RegisterDependentTask(Task.CompletedTask);

            // Only the still-live entry plus the newest completed one remain - the ten
            // interleaved completed entries above were all pruned away.
            Assert.Equal(2, DependentTaskCount(session));

            stillRunning.SetResult();
            session.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Pruning must observe a completed-but-faulted task's
    /// Exception before dropping it, or the fault surfaces as an unhandled
    /// TaskScheduler.UnobservedTaskException at finalization instead - exactly what
    /// FileSearchSession.Scan racing ObjectDisposedException out of MMapFile.GetSpan would
    /// produce if pruning didn't observe it.
    /// </summary>
    [Fact]
    public async Task RegisterDependentTask_PruningObservesFaultedEntry_NoUnobservedException()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var session = IndexedFileSession<FileOffsetIndex>.Start(
                new MMapFile(path), FileOffsetIndex.StartIndexing);

            var faulting = Task.Run(() => throw new InvalidOperationException("simulated scan fault"));
            try { await faulting; } catch { /* observed here only to know it has completed before registering */ }

            bool unobserved = false;
            void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
            {
                unobserved = true;
                e.SetObserved();
            }

            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try
            {
                session.RegisterDependentTask(faulting);
                session.RegisterDependentTask(Task.CompletedTask); // triggers the prune that drops `faulting`

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= OnUnobserved;
            }

            Assert.False(unobserved, "pruning dropped a faulted task without observing its exception");
            session.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A dependent task that reads the mapping in a loop until it observes the session's
    /// token must never see the mapping released out from under it: Dispose's join happens
    /// strictly after the task exits its loop, never concurrently with it.
    /// </summary>
    [Fact]
    public void Dispose_WaitsForDependentTask_MappingStaysValidUntilTaskObservesToken()
    {
        string path = WriteTempFile("line\n");
        try
        {
            var file = new MMapFile(path);
            var session = IndexedFileSession<FileOffsetIndex>.Start(file, FileOffsetIndex.StartIndexing);

            bool sawUseAfterFree = false;
            var task = Task.Run(() =>
            {
                var ct = session.TearingDown;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        _ = file.RequireContiguous(0, 1);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Only acceptable if the token was already cancelled when this ran -
                        // i.e. Dispose released the mapping strictly after cancelling, never
                        // concurrently with this loop still believing itself uncancelled.
                        if (!ct.IsCancellationRequested)
                            sawUseAfterFree = true;
                        break;
                    }
                }
            });
            session.RegisterDependentTask(task);

            session.Dispose(); // must not return before `task` has exited its loop

            Assert.True(task.IsCompleted);
            Assert.False(sawUseAfterFree);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
