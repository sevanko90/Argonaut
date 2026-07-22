using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Raw-viewer variant of <see cref="IndexedFileSession{TIndex}"/>: owns the <see cref="MMapFile"/>
/// mapping, the background <see cref="RawSegmentIndex"/> scanning it, and the CancellationTokenSource
/// that stops that scan - with one deliberate difference. The mapping lives for the whole
/// document lifetime while the index can be replaced (<see cref="RestartIndex"/>, the wrap-width
/// change). That difference is the whole reason this class exists: a wrap-width change must not
/// release the mapping, because a live FileSearchSession scan may hold spans over it and the
/// document view model has no way to stop that scan first (the shell owns the FindController).
/// Keeping the file fixed makes a re-index completely invisible to a running search.
///
/// Teardown ordering is IndexedFileSession's:
///
///   cancel -> join the indexing task -> release the mapping
///
/// with <see cref="RestartIndex"/> performing the same cancel-and-join for the outgoing scan
/// before starting its replacement. The scans check cancellation once per 4MB chunk, so the
/// joins resolve in low single-digit milliseconds.
///
/// Not thread-safe: create, restart and dispose from one thread (the UI thread in this app).
/// </summary>
public sealed class RawIndexSession : IDisposable
{
    private CancellationTokenSource cts;
    private bool disposed;

    public MMapFile File { get; }

    public RawSegmentIndex Index { get; private set; }

    public Task IndexingTask => this.Index.IndexingTask;

    private RawIndexSession(MMapFile file, RawSegmentIndex index, CancellationTokenSource cts)
    {
        this.File = file;
        this.Index = index;
        this.cts = cts;
    }

    /// <summary>
    /// Starts indexing <paramref name="file"/> and returns the session that now owns it.
    /// Takes ownership of <paramref name="file"/> immediately: if starting the indexer throws,
    /// the file is disposed here and the exception propagates.
    /// </summary>
    public static RawIndexSession Start(MMapFile file, int wrapWidth, IProgressReporter? progressReporter = null)
    {
        var cts = new CancellationTokenSource();
        try
        {
            var index = RawSegmentIndex.StartIndexing(file, wrapWidth, progressReporter, cts.Token);
            return new RawIndexSession(file, index, cts);
        }
        catch
        {
            cts.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Cancels the current scan, joins it, and starts a fresh index over the same mapping with
    /// a new wrap width. Joining here (a few ms - the scan halts at its next per-chunk
    /// cancellation check) rather than retiring the task to <see cref="Dispose"/> matters for
    /// memory: the task's closure references the outgoing index, whose segment log runs to
    /// hundreds of MB on a multi-GB file, and dropping the last reference now lets the GC
    /// reclaim it during the re-index instead of holding both generations until the document
    /// closes.
    /// </summary>
    public void RestartIndex(int wrapWidth, IProgressReporter? progressReporter = null)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.cts.Cancel();
        try { this.Index.IndexingTask.Wait(); } catch { /* cancellation observed here only to unblock the restart */ }
        this.cts.Dispose();

        this.cts = new CancellationTokenSource();
        this.Index = RawSegmentIndex.StartIndexing(this.File, wrapWidth, progressReporter, this.cts.Token);
    }

    /// <summary>Requests the current scan stop early. Idempotent, including after <see cref="Dispose"/>.</summary>
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

        this.File.Dispose();
        this.cts.Dispose();
    }
}
