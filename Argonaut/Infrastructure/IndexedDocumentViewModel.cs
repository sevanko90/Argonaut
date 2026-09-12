using System;
using System.Threading.Tasks;
using Argonaut.Features.Search;
using Argonaut.Shell;

namespace Argonaut.Infrastructure;

/// <summary>
/// Base for the document view models backed by an <see cref="IDocumentSession"/> (JSON, CSV,
/// NDJSON, Raw, JSON diff): the one place their shared disposal ordering - cancel, dispose the
/// rows, subclass teardown, then join/release the session - and their shared status/failure
/// plumbing lives, instead of being copied five times. A base class rather than a documented
/// pattern because the ordering is the whole point and a copied one drifts; the session is held
/// as <see cref="IDocumentSession"/> rather than a generic parameter because the session types
/// are not a family - they share a lifetime contract, not an implementation.
///
/// <see cref="Session"/> and <see cref="MappedRows"/> are ABSTRACT rather than registered by
/// the subclass calling an Attach method during load. The two are what the whole class exists
/// to sequence, and an imperative registration can be forgotten silently: a load path that
/// built its rows but never announced them would leak a live growth monitor over a mapping the
/// session had already released, with nothing failing to point at it. Abstract members make
/// that a compiler error instead. They are properties, not constructor arguments, because both
/// are created partway through LoadAsync - after an await - so neither exists yet when the
/// document is constructed; reading them live also means RawViewModel's wrap-width restart just
/// replaces its field and needs no re-registration.
///
/// Cost is nil at this layer: one instance per open document, at most one live at a time.
/// The per-element hot path (<see cref="VirtualizingItemsSourceBase.GetItem"/>) is untouched.
/// </summary>
public abstract class IndexedDocumentViewModel : ObservableObject, IDocumentViewModel
{
    private string filePath = string.Empty;
    private string statusText = string.Empty;
    private IndexFailure? indexFailure;
    private volatile bool disposed;

    /// <summary>
    /// True once <see cref="Dispose"/> has run. Written only on the UI thread (the app's
    /// threading convention), but volatile because it is also read from BACKGROUND threads:
    /// <see cref="Argonaut.Features.Json.JsonViewModel"/>'s date-hint inference tests it inside
    /// a <c>Task.Run</c> body to abandon a scan whose document has closed. Without the barrier
    /// that worker can read a cached false indefinitely and keep scanning a document already
    /// disposed - not a crash (the mapping is joined before release, and the result is applied
    /// behind a second UI-thread check), but wasted work on a multi-GB file.
    ///
    /// JsonViewModel's own flag was volatile before this base class subsumed it; the barrier is
    /// preserved here rather than dropped. Any new subclass reading this off the UI thread gets
    /// the same guarantee for free.
    /// </summary>
    protected bool IsDisposed => disposed;

    /// <summary>See <see cref="IDocumentViewModel.Origin"/>. Set once, by the load.</summary>
    public IByteOrigin? Origin { get; protected set; }

    public string FilePath
    {
        get => filePath;
        protected set => SetField(ref filePath, value);
    }

    public string StatusText
    {
        get => statusText;
        protected set => SetField(ref statusText, value);
    }

    /// <summary>See <see cref="IDocumentViewModel.IndexFailure"/>.</summary>
    public IndexFailure? IndexFailure
    {
        get => indexFailure;
        protected set => SetField(ref indexFailure, value);
    }

    /// <summary>
    /// The session this document reads through, or null before its LoadAsync has started one
    /// (and for a load that failed before getting that far). Everything below is driven from
    /// it; see the class remarks for why this is abstract rather than registered.
    /// </summary>
    protected abstract IDocumentSession? Session { get; }

    /// <summary>
    /// The row collection this document publishes, which reads <see cref="Session"/>'s mapping -
    /// so <see cref="Dispose"/> stops it BEFORE the session releases that mapping. Null until
    /// LoadAsync builds it. Named for what it is rather than "Rows" because every subclass
    /// already has a public, strongly-typed <c>Rows</c> of its own; this is the base's untyped
    /// view of the same object.
    /// </summary>
    protected abstract IDisposable? MappedRows { get; }

    /// <summary>
    /// Completed until <see cref="Session"/> exists (so a document whose load failed before
    /// starting one still satisfies IDocumentViewModel.IndexingTask - the shell awaits it in
    /// StopProgressWhenIndexedAsync), then the session's own task. Read live, so it reflects an
    /// index the session later swaps out from under a fixed mapping (RawViewModel's wrap-width
    /// restart).
    /// </summary>
    public Task IndexingTask => Session?.IndexingTask ?? Task.CompletedTask;

    public abstract object? Toolbar { get; }

    public abstract ISearchNavigator? CreateSearchNavigator();

    public abstract bool CanHandleFileType(FileTypeDetector.FileKind fileType);

    /// <summary>
    /// Fire-and-forget monitor: awaits the session's current indexing task, then calls
    /// <see cref="OnIndexingCompleted"/> or <see cref="OnIndexingFailed"/> - skipped if this
    /// document is disposed by the time the await resumes, or if the session has since moved on
    /// to a different task (a RawViewModel wrap-width restart retired the one we awaited; see
    /// <see cref="RawViewModel.IndexGeneration"/>'s remarks).
    ///
    /// The staleness guard compares the awaited TASK against
    /// <see cref="IDocumentSession.IndexingTask"/>, which is the question directly: is what I
    /// awaited still what this document is running?
    ///
    /// MUST be called by the subclass's LoadAsync (or SetWrapWidth-style re-index) BEFORE that
    /// method returns/completes - the shell registers its own continuation on the same
    /// IndexingTask afterwards, and StopProgressWhenIndexedAsync's ordering guarantee (the
    /// document's own final StatusText write must happen first) depends on this one having
    /// been started already. No-op if no session exists yet.
    /// </summary>
    protected void MonitorIndexing()
    {
        if (Session is { } session)
            _ = MonitorIndexingAsync(session);
    }

    private async Task MonitorIndexingAsync(IDocumentSession session)
    {
        var task = session.IndexingTask;
        try
        {
            await task;
        }
        catch
        {
            if (disposed || !ReferenceEquals(task, session.IndexingTask))
                return;

            OnIndexingFailed(session.Failure);
            return;
        }

        if (disposed || !ReferenceEquals(task, session.IndexingTask))
            return;

        OnIndexingCompleted();
    }

    /// <summary>
    /// Indexing finished. Owns the whole completion reaction, not just the status string - see
    /// each override for what varies (JsonViewModel re-scores schema roots; NdJsonViewModel
    /// preserves its "Selected line" suffix; JsonDiffViewModel summarises the comparison).
    /// Takes no argument: every subclass reports from state it already has (its own row/token
    /// count), so the indexer this once handed over was read by one override out of four, and
    /// that one had a typed count of its own. Default no-op, for a subclass with nothing to say
    /// on completion.
    /// </summary>
    protected virtual void OnIndexingCompleted()
    {
    }

    /// <summary>
    /// Indexing stopped early. Nullable because null is CANCELLATION, not failure - the task
    /// faults either way. Sets <see cref="IndexFailure"/> itself (the base does not) for the
    /// same reason completion isn't a describer: a string-returning hook cannot also run
    /// JsonViewModel's schema re-score, so hooks own their whole reaction, not just the text.
    /// Default no-op - see <see cref="OnIndexingCompleted"/>'s remarks.
    /// </summary>
    protected virtual void OnIndexingFailed(IndexFailure? failure)
    {
    }

    /// <summary>Subclass teardown, run between rows disposal and session disposal - e.g.
    /// NdJsonViewModel's nested per-line JsonViewModel and settings-handler unsubscription.</summary>
    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        // Idempotent - see IDocumentViewModel's lifetime contract: a document can be disposed
        // from both the shell's outgoing-document path and its hosting view's own detach
        // handler.
        if (disposed)
            return;
        disposed = true;

        // Captured once: DisposeCore runs subclass teardown in the middle of this sequence, and
        // the session must be the same one on both sides of it.
        var session = Session;

        // Stop first so background scans start winding down promptly; rows next (its growth
        // monitor reads the mapping), then subclass teardown, then the session join/release -
        // the one ordering every subclass used to hand-encode.
        session?.RequestStop();
        MappedRows?.Dispose();
        DisposeCore();
        session?.Dispose();
    }
}
