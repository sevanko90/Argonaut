using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// Owns the lifetime trio behind one open document: the <see cref="IByteSource"/> its bytes
/// come from (a mapping, an in-memory payload, a streamed download), the
/// background <typeparamref name="TIndex"/> scanning it, and the CancellationTokenSource
/// that stops that scan. Its whole purpose is to encode the teardown ordering in exactly
/// one place:
///
///   cancel -> join the indexing task -> join dependent tasks -> release the mapping
///
/// The join steps are what make the release safe: the scans dereference the source via
/// cached pointers, and releasing a mapping out from under a still-running scan is a native
/// use-after-free, not a catchable .NET exception (see CLAUDE.md / MMapFile). The scans
/// check cancellation every ~65536 tokens / 4MB chunk, so the joins resolve in low
/// single-digit milliseconds even on multi-GB files.
///
/// Additional readers start through <see cref="StartDependentRead{TResult}"/>, which
/// registers only background completion. UI continuations are never joined. Search owns
/// independent chunk mappings, so its lifetime does not constrain this session's release.
///
/// Not thread-safe: create, register and dispose from one thread (the UI thread in this
/// app). The indexing/dependent tasks themselves of course run in the background.
/// </summary>
public sealed class IndexedSourceSession<TIndex> : IDocumentSession where TIndex : class, IBackgroundIndex
{
    private readonly CancellationTokenSource cts;
    private readonly List<Task> dependentTasks = new();
    private bool disposed;

    public IByteSource Bytes { get; }

    public TIndex Index { get; }

    public Task IndexingTask => this.Index.IndexingTask;

    /// <summary>See <see cref="IDocumentSession.Failure"/>.</summary>
    public IndexFailure? Failure => this.Index.Failure;

    /// <summary>See <see cref="IDocumentSession.TearingDown"/>.</summary>
    public CancellationToken TearingDown => this.cts.Token;

    private IndexedSourceSession(IByteSource bytes, TIndex index, CancellationTokenSource cts)
    {
        this.Bytes = bytes;
        this.Index = index;
        this.cts = cts;
    }

    /// <summary>
    /// Starts indexing <paramref name="bytes"/> and returns the session that now owns it.
    /// Takes ownership of <paramref name="bytes"/> immediately: if the factory throws, the
    /// source is released here and the exception propagates.
    /// </summary>
    /// <param name="bytes">The bytes to index; owned by the returned session from this point on.</param>
    /// <param name="startIndexing">
    /// Indexer factory - both real indexers' StartIndexing methods match this shape, so
    /// call sites pass a method group (e.g. <c>JsonStructureIndex.StartIndexing</c>).
    /// </param>
    /// <param name="progressReporter">Optional progress reporter forwarded to the factory.</param>
    public static IndexedSourceSession<TIndex> Start(
        IByteSource bytes,
        Func<IByteSource, IProgressReporter?, CancellationToken, TIndex> startIndexing,
        IProgressReporter? progressReporter = null)
    {
        var cts = new CancellationTokenSource();
        try
        {
            var index = startIndexing(bytes, progressReporter, cts.Token);
            return new IndexedSourceSession<TIndex>(bytes, index, cts);
        }
        catch
        {
            cts.Dispose();
            bytes.Release();
            throw;
        }
    }

    /// <summary>Starts a mapping reader on the pool and registers only its background
    /// completion. UI continuations awaiting the returned task must never be joined by
    /// Dispose: they need the very thread performing disposal.</summary>
    public Task<TResult> StartDependentRead<TResult>(Func<CancellationToken, Task<TResult>> read)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);
        var tearingDown = this.TearingDown;
        var reading = Task.Run(() => read(tearingDown));
        RegisterDependentTask(reading);
        return reading;
    }

    /// <summary>
    /// Registers a background-only task (never a UI-context continuation) that dereferences <see cref="Bytes"/> (date-hint inference,
    /// JSON path resolution) so <see cref="Dispose"/> joins it before releasing the mapping.
    /// No-op if the session is already disposed - <see cref="TearingDown"/> is cancelled by
    /// then, so such a task dies immediately without touching the source.
    ///
    /// Prunes already-completed entries first (O(n) with n in single digits, on the UI
    /// thread) so a long session with many search-term changes doesn't accumulate one Task
    /// reference per search forever. A completed entry's exception is observed before it is
    /// dropped - a registered task racing an <see cref="ObjectDisposedException"/> out of
    /// <see cref="MMapFile.GetContiguousSpan"/> faults; dropping that unobserved would raise
    /// <see cref="System.Threading.Tasks.TaskScheduler.UnobservedTaskException"/> at
    /// finalization instead.
    /// </summary>
    internal void RegisterDependentTask(Task task)
    {
        if (this.disposed)
            return;

        for (int i = this.dependentTasks.Count - 1; i >= 0; i--)
        {
            if (!this.dependentTasks[i].IsCompleted)
                continue;

            _ = this.dependentTasks[i].Exception;
            this.dependentTasks.RemoveAt(i);
        }

        this.dependentTasks.Add(task);
    }

    /// <summary>
    /// Requests the scan stop early. Idempotent, including after <see cref="Dispose"/> - the
    /// nested per-line JsonViewModel is disposed from two independent paths (its owning
    /// NdJsonViewModel, and its JsonView's own detach handler when the visual tree tears
    /// down), so a second RequestStop/Dispose pair on the same session is expected, not a bug.
    /// </summary>
    public void RequestStop()
    {
        if (this.disposed)
            return;

        this.cts.Cancel();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        this.cts.Cancel();
        try { this.Index.IndexingTask.Wait(); } catch { /* cancellation/failure observed here only to unblock disposal */ }
        foreach (var task in this.dependentTasks)
        {
            try { task.Wait(); } catch { /* same */ }
        }

        this.Bytes.Release();
        this.cts.Dispose();
    }
}
