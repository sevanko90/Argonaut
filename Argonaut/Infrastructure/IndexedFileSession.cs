using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// Owns the lifetime trio behind one open file: the <see cref="MMapFile"/> mapping, the
/// background <typeparamref name="TIndex"/> scanning it, and the CancellationTokenSource
/// that stops that scan. Its whole purpose is to encode the teardown ordering in exactly
/// one place:
///
///   cancel -> join the indexing task -> join dependent tasks -> release the mapping
///
/// The join steps are what make the release safe: the scans dereference the mapping via
/// cached pointers, and disposing it out from under a still-running scan is a native
/// use-after-free, not a catchable .NET exception (see CLAUDE.md / MMapFile). The scans
/// check cancellation every ~65536 tokens / 4MB chunk, so the joins resolve in low
/// single-digit milliseconds even on multi-GB files.
///
/// What the session CANNOT know about: readers it didn't start, such as FileSearchSession
/// scans holding spans over <see cref="File"/>. Callers must stop those before disposing
/// the session - MainWindow does this by awaiting FindController.Detach/Stop before any
/// content swap that leads to a view-model (and therefore session) dispose.
///
/// Not thread-safe: create, register and dispose from one thread (the UI thread in this
/// app). The indexing/dependent tasks themselves of course run in the background.
/// </summary>
public sealed class IndexedFileSession<TIndex> : IDisposable where TIndex : class, IFileIndexer
{
    private readonly CancellationTokenSource cts;
    private readonly List<Task> dependentTasks = new();
    private bool disposed;

    public MMapFile File { get; }

    public TIndex Index { get; }

    public Task IndexingTask => this.Index.IndexingTask;

    /// <summary>
    /// Cancelled when the session is cancelled/disposed. Hand this to any background work
    /// that reads <see cref="File"/> so it stops before the mapping is released.
    /// </summary>
    public CancellationToken Token => this.cts.Token;

    private IndexedFileSession(MMapFile file, TIndex index, CancellationTokenSource cts)
    {
        this.File = file;
        this.Index = index;
        this.cts = cts;
    }

    /// <summary>
    /// Starts indexing <paramref name="file"/> and returns the session that now owns it.
    /// Takes ownership of <paramref name="file"/> immediately: if the factory throws, the
    /// file is disposed here and the exception propagates.
    /// </summary>
    /// <param name="file">The mapping to index; owned by the returned session from this point on.</param>
    /// <param name="startIndexing">
    /// Indexer factory - both real indexers' StartIndexing methods match this shape, so
    /// call sites pass a method group (e.g. <c>JsonStructureIndex.StartIndexing</c>).
    /// </param>
    /// <param name="progressReporter">Optional progress reporter forwarded to the factory.</param>
    public static IndexedFileSession<TIndex> Start(
        MMapFile file,
        Func<MMapFile, IProgressReporter?, CancellationToken, TIndex> startIndexing,
        IProgressReporter? progressReporter = null)
    {
        var cts = new CancellationTokenSource();
        try
        {
            var index = startIndexing(file, progressReporter, cts.Token);
            return new IndexedFileSession<TIndex>(file, index, cts);
        }
        catch
        {
            cts.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Registers a background task that dereferences <see cref="File"/> (e.g. date-hint
    /// inference) so <see cref="Dispose"/> joins it before releasing the mapping. No-op if
    /// the session is already disposed - <see cref="Token"/> is cancelled by then, so such
    /// a task dies immediately without touching the file.
    /// </summary>
    public void RegisterDependentTask(Task task)
    {
        if (this.disposed)
            return;

        this.dependentTasks.Add(task);
    }

    /// <summary>
    /// Requests the scan stop early. Idempotent, including after <see cref="Dispose"/> - the
    /// nested per-line JsonViewModel is disposed from two independent paths (its owning
    /// NdJsonViewModel, and its JsonView's own detach handler when the visual tree tears
    /// down), so a second Cancel/Dispose pair on the same session is expected, not a bug.
    /// </summary>
    public void Cancel()
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

        this.File.Dispose();
        this.cts.Dispose();
    }
}
