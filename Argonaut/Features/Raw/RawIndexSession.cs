using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Raw-viewer variant of <see cref="IndexedSourceSession{TIndex}"/>: owns the <see cref="IByteSource"/>
/// its bytes come from, the background <see cref="RawSegmentIndex"/> scanning it, and the CancellationTokenSource
/// that stops that scan - with one deliberate difference. The mapping lives for the whole
/// document lifetime while the index can be replaced (<see cref="RestartIndex"/>, the wrap-width
/// change). That difference is the whole reason this class exists: keeping the mapping fixed
/// across a wrap-width change avoids the cost of a multi-GB unmap/remap and lets a running
/// search continue uninterrupted (its match offsets are unaffected by re-wrapping).
///
/// Because of that, this session tracks TWO lifetimes rather than IndexedSourceSession's one -
/// see <see cref="TearingDown"/>. Teardown ordering is otherwise IndexedSourceSession's:
///
///   cancel -> join the indexing task -> release the mapping
///
/// with <see cref="RestartIndex"/> performing the same cancel-and-join for the outgoing scan
/// before starting its replacement, deliberately keeping the mapping alive across it.
/// The scans check cancellation once per 4MB chunk, so the joins resolve in low single-digit
/// milliseconds.
///
/// Not thread-safe: create, restart and dispose from one thread (the UI thread in this app).
/// </summary>
public sealed class RawIndexSession : IDocumentSession
{
    private readonly CancellationTokenSource mappingCts;
    private CancellationTokenSource indexCts;
    private bool disposed;

    public IByteSource Bytes { get; }

    public RawSegmentIndex Index { get; private set; }

    /// <summary>See <see cref="IDocumentSession.IndexingTask"/>. Live by construction:
    /// <see cref="RestartIndex"/> replaces <see cref="Index"/>, so this starts returning the new
    /// scan's task the moment the wrap width changes - which is what lets the completion monitor
    /// recognise the retired one.</summary>
    public Task IndexingTask => this.Index.IndexingTask;

    /// <summary>See <see cref="IDocumentSession.Failure"/>.</summary>
    public IndexFailure? Failure => this.Index.Failure;

    /// <summary>
    /// See <see cref="IDocumentSession.TearingDown"/>. Fires only when this session is torn
    /// down - unlike <see cref="indexCts"/> it survives a <see cref="RestartIndex"/>, which is
    /// the whole reason the two are separate. A find reveal links this one: it deliberately
    /// re-resolves across a wrap-width change (see <see cref="RawViewModel.IndexGeneration"/>),
    /// so linking the per-index source would cancel a reveal the user can still see the point
    /// of.
    /// </summary>
    public CancellationToken TearingDown => this.mappingCts.Token;

    private RawIndexSession(IByteSource bytes, RawSegmentIndex index, CancellationTokenSource mappingCts, CancellationTokenSource indexCts)
    {
        this.Bytes = bytes;
        this.Index = index;
        this.mappingCts = mappingCts;
        this.indexCts = indexCts;
    }

    /// <summary>
    /// Starts indexing <paramref name="bytes"/> and returns the session that now owns it.
    /// Takes ownership of <paramref name="bytes"/> immediately: if starting the indexer throws,
    /// the file is disposed here and the exception propagates.
    /// </summary>
    public static RawIndexSession Start(IByteSource bytes, int wrapWidth, IProgressReporter? progressReporter = null)
    {
        var mappingCts = new CancellationTokenSource();
        var indexCts = CancellationTokenSource.CreateLinkedTokenSource(mappingCts.Token);
        try
        {
            var index = RawSegmentIndex.StartIndexing(bytes, wrapWidth, progressReporter, indexCts.Token);
            return new RawIndexSession(bytes, index, mappingCts, indexCts);
        }
        catch
        {
            indexCts.Dispose();
            mappingCts.Dispose();
            bytes.Release();
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
    /// closes. The mapping is deliberately not moving, so a find scan over the same path (which
    /// owns its own mapping anyway) and a linked reveal both ride through untouched.
    /// </summary>
    public void RestartIndex(int wrapWidth, IProgressReporter? progressReporter = null)
    {
        ObjectDisposedException.ThrowIf(this.disposed, this);

        this.indexCts.Cancel();
        try { this.Index.IndexingTask.Wait(); } catch { /* cancellation observed here only to unblock the restart */ }
        this.indexCts.Dispose();

        this.indexCts = CancellationTokenSource.CreateLinkedTokenSource(this.mappingCts.Token);
        this.Index = RawSegmentIndex.StartIndexing(this.Bytes, wrapWidth, progressReporter, this.indexCts.Token);
    }

    /// <summary>
    /// Requests the current scan stop early, by cancelling the mapping source - the per-index
    /// source is linked from it, so this cascades. Idempotent, including after
    /// <see cref="Dispose"/>. Cancelling the mapping source rather than just the index one is
    /// what makes <see cref="TearingDown"/> fire here, so a linked reveal stops at the same
    /// moment the scan does.
    /// </summary>
    public void RequestStop()
    {
        if (this.disposed)
            return;

        this.mappingCts.Cancel();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        this.mappingCts.Cancel();
        try { this.Index.IndexingTask.Wait(); } catch { /* cancellation/failure observed here only to unblock disposal */ }

        this.Bytes.Release();
        this.indexCts.Dispose();
        this.mappingCts.Dispose();
    }
}
