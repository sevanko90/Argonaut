using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Owns the lifetime pair behind one array-as-table document: an
/// <see cref="IndexedSourceSession{TIndex}"/> over the array's own byte range, plus the
/// <see cref="JsonArrayElementIndex"/> that reads that session's token index for as long as it
/// runs. Same job <see cref="Diff.JsonDiffSession"/> does for a diff, with one file session
/// instead of two.
///
/// The mapping is the ARRAY, not the file: <c>[</c>…<c>]</c> is a valid JSON document on its
/// own, so the table opens an independent sub-range mapping and shares nothing with the JSON
/// document it was opened from - which is required, not merely tidy, because the shell disposes
/// the outgoing document before publishing this one. Root token 0 is therefore always the array.
///
/// <b>Why this type exists rather than the view model holding both.</b>
/// <see cref="IDocumentSession.IndexingTask"/> is what the document (and through it the shell's
/// progress reporting) waits on, and <see cref="IndexedDocumentViewModel.IndexingTask"/> is
/// non-virtual by design. The token scan completing is NOT the moment this table stops growing -
/// the element index publishes one final stride afterwards - so the task that answers "is this
/// document still growing" is the element index's. Stating that here puts the answer in the type
/// that owns both tasks, exactly as JsonDiffSession reports the diff's task rather than either
/// side's.
///
/// Teardown ordering is the other reason, and it is not negotiable:
///
///   1. cancel the element walk (its token is linked over the file session's)
///   2. join the element walk - after this nothing reads the token index
///   3. dispose the file session (cancels, joins its scan and dependents, releases the mapping)
///
/// Idempotent, same contract as its siblings. Not thread-safe: create and dispose from the UI
/// thread.
/// </summary>
public sealed class JsonArrayTableSession : IDocumentSession
{
    private readonly CancellationTokenSource elementCts;
    private bool disposed;

    /// <summary>The sub-range mapping and its token scan. The element index reads this index for
    /// its whole lifetime, which is what fixes the disposal order below.</summary>
    public IndexedSourceSession<JsonStructureIndex> Inner { get; }

    /// <summary>Ordinal addressing over the array's direct children - the table's row source.</summary>
    public JsonArrayElementIndex Elements { get; }

    /// <summary>
    /// See <see cref="IDocumentSession.TearingDown"/>. Linked over the file session's, so it
    /// fires whether teardown starts here or there.
    /// </summary>
    public CancellationToken TearingDown => this.elementCts.Token;

    /// <summary>
    /// See <see cref="IDocumentSession.IndexingTask"/>, and this class's remarks for why it is
    /// the element index's task and not the token scan's.
    /// </summary>
    public Task IndexingTask => this.Elements.IndexingTask;

    /// <summary>
    /// See <see cref="IDocumentSession.Failure"/>. The token scan's failure first: that is the
    /// one a malformed byte range produces, and the one carrying a line/column to report. The
    /// element walk's own is a fallback - it reads no file, so a fault there is a defect rather
    /// than bad data, but reporting it beats swallowing it.
    /// </summary>
    public IndexFailure? Failure => this.Inner.Failure ?? this.Elements.Failure;

    private JsonArrayTableSession(IndexedSourceSession<JsonStructureIndex> inner, JsonArrayElementIndex elements, CancellationTokenSource elementCts)
    {
        this.Inner = inner;
        this.Elements = elements;
        this.elementCts = elementCts;
    }

    /// <summary>
    /// Maps <paramref name="length"/> bytes of <paramref name="path"/> starting at
    /// <paramref name="offset"/> - which must be exactly the array's <c>[</c>…<c>]</c> range -
    /// indexes it as a JSON document in its own right, and starts walking its elements.
    /// Ownership of everything started transfers to the returned session; a failure partway
    /// disposes what was already started before rethrowing.
    /// </summary>
    public static JsonArrayTableSession Start(string path, long offset, long length, IProgressReporter? progressReporter = null)
    {
        var inner = IndexedSourceSession<JsonStructureIndex>.Start(
            new MMapFile(path, offset, length), JsonStructureIndex.StartIndexing, progressReporter);

        var elementCts = CancellationTokenSource.CreateLinkedTokenSource(inner.TearingDown);
        try
        {
            // Token 0: the mapping IS the array, so its root value is the array itself.
            var elements = JsonArrayElementIndex.Start(inner.Index, 0, elementCts.Token);
            return new JsonArrayTableSession(inner, elements, elementCts);
        }
        catch
        {
            elementCts.Dispose();
            inner.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Asks the element walk and the token scan to stop, and fires <see cref="TearingDown"/>.
    /// Cooperative: returns immediately, and nothing has let go of the mapping yet when it does.
    /// Idempotent, including after <see cref="Dispose"/>.
    /// </summary>
    public void RequestStop()
    {
        if (this.disposed)
            return;

        RequestStopCore();
    }

    private void RequestStopCore()
    {
        this.elementCts.Cancel();
        this.Inner.RequestStop();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        // The ordering here is not negotiable - see the class remarks.
        RequestStopCore();
        try { this.Elements.IndexingTask.Wait(); } catch { /* cancellation/failure observed only to unblock disposal */ }

        this.Inner.Dispose();
        this.elementCts.Dispose();
    }
}
