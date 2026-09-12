using System.Collections.Concurrent;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

public class DependentReadContextTests
{
    [Fact]
    public async Task DisposeJoinsReaderWithoutNeedingItsCallersSynchronizationContext()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "[1]");
        var uiContext = new QueuedContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? reading = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            try
            {
                using var session = IndexedSourceSession<JsonStructureIndex>.Start(new MMapFile(path), JsonStructureIndex.StartIndexing);
                session.IndexingTask.GetAwaiter().GetResult();
                reading = session.StartDependentRead(async tearingDown =>
                {
                    Assert.Null(SynchronizationContext.Current);
                    entered.SetResult();
                    await release.Task;
                    await session.IndexingTask;
                    // Resolving after an await must still run away from the UI thread.
                    Assert.Null(SynchronizationContext.Current);
                    return (await JsonPathResolver.ResolveAsync(session.Index, session.Bytes, "$[0]")).TokenIndex!.Value;
                });
                // Model a UI-originated flow awaiting the reader separately.
                _ = ApplyOnUiAsync(reading);
                session.Dispose();
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(reading!.IsCompletedSuccessfully);
            Assert.NotEmpty(uiContext.Callbacks);
        }
        finally
        {
            release.TrySetResult();
            while (uiContext.Callbacks.TryDequeue(out var callback)) callback.Action(callback.State);
            thread.Join(5000);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task JsonViewModelCanCloseWithDateInferenceUiContinuationPending()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, "[1,2,3]");
        var uiContext = new QueuedContext();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(uiContext);
            try
            {
                var document = new JsonViewModel();
                var loading = document.LoadAsync(path);
                while (!loading.IsCompleted)
                {
                    if (uiContext.Callbacks.TryDequeue(out var callback)) callback.Action(callback.State);
                    else Thread.Yield();
                }
                loading.GetAwaiter().GetResult();
                document.Dispose();
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
        }) { IsBackground = true };
        thread.Start();
        try { await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally
        {
            // Also releases a regressed implementation's blocked disposal after timeout.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (thread.IsAlive && DateTime.UtcNow < deadline)
            {
                if (uiContext.Callbacks.TryDequeue(out var callback)) callback.Action(callback.State);
                else Thread.Yield();
            }
            File.Delete(path);
        }
    }

    private static async Task ApplyOnUiAsync(Task<int> reading) => _ = await reading;

    private sealed class QueuedContext : SynchronizationContext
    {
        public ConcurrentQueue<(SendOrPostCallback Action, object? State)> Callbacks { get; } = new();
        public override void Post(SendOrPostCallback action, object? state) => Callbacks.Enqueue((action, state));
    }
}
