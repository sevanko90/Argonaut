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
        public string ItemNoun => "items";
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
            session.Token.Register(() => tokenCancelled.SetResult());

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
            session.Cancel();
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

            Assert.False(session.Token.IsCancellationRequested);
            session.Cancel();
            session.Cancel();
            Assert.True(session.Token.IsCancellationRequested);
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
}
