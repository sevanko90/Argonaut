using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Owns the lifetime trio behind one diff: TWO <see cref="IndexedSourceSession{TIndex}"/>s
/// (each guarding its own mapping with the cancel → join → release ordering
/// docs/architecture.md mandates) plus the <see cref="JsonDiffIndex"/> reading spans from
/// BOTH mappings. The diff task cannot be a RegisterDependentTask of either side - neither
/// session may release its mapping while it runs - so this type encodes the only safe
/// teardown ordering in one place:
///
///   1. cancel the diff (its token links both sides' tokens)
///   2. join the diff task - after this nothing reads either mapping
///   3. dispose Left (which cancels, joins its scan and dependents, releases its mapping)
///   4. dispose Right
///
/// Idempotent, same as IndexedSourceSession - the diff view model and the view's detach
/// handler both call it. Not thread-safe: create and dispose from the UI thread.
/// </summary>
public sealed class JsonDiffSession : IDocumentSession
{
    private readonly CancellationTokenSource diffCts;
    private readonly Task hashReleaseTask;
    private bool disposed;

    public IndexedSourceSession<JsonStructureIndex> Left { get; }

    public IndexedSourceSession<JsonStructureIndex> Right { get; }

    public JsonDiffIndex Diff { get; }

    public IByteOrigin LeftOrigin { get; }

    public IByteOrigin RightOrigin { get; }

    /// <summary>
    /// See <see cref="IDocumentSession.TearingDown"/>. The diff's own source, which is linked
    /// over both sides' - so it fires whether teardown starts here or at either side, and a
    /// find reveal linking it stops before either mapping goes.
    /// </summary>
    public CancellationToken TearingDown => this.diffCts.Token;

    /// <summary>
    /// See <see cref="IDocumentSession.IndexingTask"/>. The diff worker's task, not either
    /// side's: it internally waits for both indexes before comparing, so it is the last thing
    /// to finish and the only completion the document reacts to. Never replaced - there is no
    /// diff equivalent of RawIndexSession's restart.
    /// </summary>
    public Task IndexingTask => this.Diff.IndexingTask;

    /// <summary>
    /// See <see cref="IDocumentSession.Failure"/>. Always null HERE, deliberately: a failure in
    /// this session belongs to the left or the right document, and flattening it to one
    /// unattributed <see cref="IndexFailure"/> would drop the only part the user needs - which
    /// file failed. <see cref="Argonaut.Features.Json.Diff.JsonDiffViewModel"/> reads
    /// <see cref="Left"/>/<see cref="Right"/> directly and prefixes the message with the side,
    /// which it can do and this cannot, because only it knows the display names. Note a side
    /// failure does NOT fault <see cref="IndexingTask"/>: the diff completes normally over an
    /// empty index, so the view model does its attribution on the completion path too.
    /// </summary>
    public IndexFailure? Failure => null;

    private JsonDiffSession(IByteOrigin leftOrigin, IByteOrigin rightOrigin,
        IndexedSourceSession<JsonStructureIndex> left, IndexedSourceSession<JsonStructureIndex> right,
        JsonDiffIndex diff, CancellationTokenSource diffCts)
    {
        this.LeftOrigin = leftOrigin;
        this.RightOrigin = rightOrigin;
        this.Left = left;
        this.Right = right;
        this.Diff = diff;
        this.diffCts = diffCts;
        this.hashReleaseTask = ReleaseContentHashesWhenFinishedAsync(left, right, diff);
    }

    /// <summary>Completes after both index writers and the diff reader have stopped and their
    /// now-unused content-hash logs have been released. Internal for deterministic tests.</summary>
    internal Task HashReleaseTask => this.hashReleaseTask;

    /// <summary>
    /// Opens a source over each origin, starts both indexers (with content hashes - the whole point) and
    /// the diff worker, which internally waits for both indexes to complete before
    /// comparing. Ownership of everything started transfers to the returned session; a
    /// failure to open the second file disposes the first side before rethrowing.
    /// </summary>
    public static JsonDiffSession Start(IByteOrigin leftOrigin, IByteOrigin rightOrigin,
        IProgressReporter? leftProgress = null, IProgressReporter? rightProgress = null,
        IProgressReporter? diffProgress = null)
    {
        var options = new JsonIndexOptions { ComputeContentHashes = true };

        // The lambda (not a method group) closes over the options - see the StartIndexing
        // overload remarks for why the original signature had to stay intact.
        var left = IndexedSourceSession<JsonStructureIndex>.Start(
            leftOrigin.Open(), (f, r, ct) => JsonStructureIndex.StartIndexing(f, options, r, ct), leftProgress);

        IndexedSourceSession<JsonStructureIndex> right;
        try
        {
            right = IndexedSourceSession<JsonStructureIndex>.Start(
                rightOrigin.Open(), (f, r, ct) => JsonStructureIndex.StartIndexing(f, options, r, ct), rightProgress);
        }
        catch
        {
            left.Dispose();
            throw;
        }

        var diffCts = CancellationTokenSource.CreateLinkedTokenSource(left.TearingDown, right.TearingDown);
        try
        {
            var diff = JsonDiffIndex.Start(left.Index, left.Bytes, right.Index, right.Bytes, diffProgress, diffCts.Token);
            return new JsonDiffSession(leftOrigin, rightOrigin, left, right, diff, diffCts);
        }
        catch
        {
            diffCts.Dispose();
            left.Dispose();
            right.Dispose();
            throw;
        }
    }

    private static async Task ReleaseContentHashesWhenFinishedAsync(
        IndexedSourceSession<JsonStructureIndex> left,
        IndexedSourceSession<JsonStructureIndex> right,
        JsonDiffIndex diff)
    {
        try
        {
            await Task.WhenAll(left.IndexingTask, right.IndexingTask, diff.IndexingTask).ConfigureAwait(false);
        }
        catch
        {
            // Failed/cancelled producers are still safe to release once every task stopped.
        }
        finally
        {
            left.Index.ReleaseContentHashes();
            right.Index.ReleaseContentHashes();
        }
    }

    /// <summary>
    /// Requests both sides' scans stop early, along with the diff itself, and fires
    /// <see cref="TearingDown"/>. Idempotent, including after
    /// <see cref="Dispose"/> - same contract as <see cref="IndexedSourceSession{TIndex}.RequestStop"/>
    /// and <see cref="Argonaut.Features.Raw.RawIndexSession.RequestStop"/>. Harmless to call
    /// before Dispose: <see cref="diffCts"/> is already a linked source over both sides'
    /// tokens, so cancelling them was always going to cancel the diff too - but calling this
    /// explicitly starts everything winding down at once rather than side by side.
    /// </summary>
    public void RequestStop()
    {
        if (this.disposed)
            return;

        RequestStopCore();
    }

    private void RequestStopCore()
    {
        this.diffCts.Cancel();
        this.Left.RequestStop();
        this.Right.RequestStop();
    }

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        // The ordering here is not negotiable - see the class remarks.
        RequestStopCore();
        try { this.Diff.IndexingTask.Wait(); } catch { /* cancellation/failure observed only to unblock disposal */ }

        this.Left.Dispose();
        this.Right.Dispose();
        try { this.hashReleaseTask.Wait(); } catch { /* release task deliberately absorbs producer failures */ }
        this.diffCts.Dispose();
    }
}
