using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json.Diff;

/// <summary>
/// Owns the lifetime trio behind one diff: TWO <see cref="IndexedFileSession{TIndex}"/>s
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
/// Idempotent, same as IndexedFileSession - the diff view model and the view's detach
/// handler both call it. Not thread-safe: create and dispose from the UI thread.
/// </summary>
public sealed class JsonDiffSession : IDisposable
{
    private readonly CancellationTokenSource diffCts;
    private readonly Task hashReleaseTask;
    private bool disposed;

    public IndexedFileSession<JsonStructureIndex> Left { get; }

    public IndexedFileSession<JsonStructureIndex> Right { get; }

    public JsonDiffIndex Diff { get; }

    public string LeftPath { get; }

    public string RightPath { get; }

    private JsonDiffSession(string leftPath, string rightPath,
        IndexedFileSession<JsonStructureIndex> left, IndexedFileSession<JsonStructureIndex> right,
        JsonDiffIndex diff, CancellationTokenSource diffCts)
    {
        this.LeftPath = leftPath;
        this.RightPath = rightPath;
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
    /// Opens both files, starts both indexers (with content hashes - the whole point) and
    /// the diff worker, which internally waits for both indexes to complete before
    /// comparing. Ownership of everything started transfers to the returned session; a
    /// failure to open the second file disposes the first side before rethrowing.
    /// </summary>
    public static JsonDiffSession Start(string leftPath, string rightPath,
        IProgressReporter? leftProgress = null, IProgressReporter? rightProgress = null,
        IProgressReporter? diffProgress = null)
    {
        var options = new JsonIndexOptions { ComputeContentHashes = true };

        // The lambda (not a method group) closes over the options - see the StartIndexing
        // overload remarks for why the original signature had to stay intact.
        var left = IndexedFileSession<JsonStructureIndex>.Start(
            new MMapFile(leftPath), (f, r, ct) => JsonStructureIndex.StartIndexing(f, options, r, ct), leftProgress);

        IndexedFileSession<JsonStructureIndex> right;
        try
        {
            right = IndexedFileSession<JsonStructureIndex>.Start(
                new MMapFile(rightPath), (f, r, ct) => JsonStructureIndex.StartIndexing(f, options, r, ct), rightProgress);
        }
        catch
        {
            left.Dispose();
            throw;
        }

        var diffCts = CancellationTokenSource.CreateLinkedTokenSource(left.Token, right.Token);
        try
        {
            var diff = JsonDiffIndex.Start(left.Index, left.File, right.Index, right.File, diffProgress, diffCts.Token);
            return new JsonDiffSession(leftPath, rightPath, left, right, diff, diffCts);
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
        IndexedFileSession<JsonStructureIndex> left,
        IndexedFileSession<JsonStructureIndex> right,
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

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;

        // The ordering here is not negotiable - see the class remarks.
        this.diffCts.Cancel();
        try { this.Diff.IndexingTask.Wait(); } catch { /* cancellation/failure observed only to unblock disposal */ }

        this.Left.Dispose();
        this.Right.Dispose();
        try { this.hashReleaseTask.Wait(); } catch { /* release task deliberately absorbs producer failures */ }
        this.diffCts.Dispose();
    }
}
