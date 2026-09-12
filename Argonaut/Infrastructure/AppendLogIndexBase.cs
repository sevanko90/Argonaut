using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// Shared base for the background scanners that publish fixed-size records into a
/// <see cref="SegmentedAppendLog{T}"/> from a single writer thread while UI-thread readers
/// consume them lock-free (FileOffsetIndex, JsonStructureIndex, SearchSession).
///
/// The base owns the log and the cold waiter machinery (WaitForCountAsync / MarkComplete).
/// The hot scan loops stay in the derived classes and interact with the base only through
/// <see cref="items"/> and the inlined <see cref="OnItemsPublished"/> check, so this
/// extraction adds zero indirection on the per-record hot path.
/// </summary>
public abstract class AppendLogIndexBase<T> where T : struct
{
    // Single-writer (the scan task) / multi-reader (UI). The log's volatile Count
    // publication is what lets readers run lock-free - see SegmentedAppendLog for the
    // full reasoning.
    protected readonly SegmentedAppendLog<T> items = new();

    // Guards ONLY the cold waiter machinery below (registration and completion of the
    // outstanding waits). Nothing on the per-record hot path takes this lock.
    private readonly Lock sync = new();

    // Every outstanding wait with the count it is waiting for. A LIST rather than the single
    // shared TaskCompletionSource this used to hold: the waits genuinely overlap and genuinely
    // want different targets - a document's date-scheme inference waits for its whole sample
    // while a path resolve waits for the next batch - and one slot cannot hold both. It used to
    // be overwritten by whichever wait asked for a larger target, which stranded the smaller
    // one's task forever: nothing completed it, MarkComplete only ever saw the newer slot, and
    // the task the caller had already awaited never finished. That is a hang, not a delay - it
    // deadlocked IndexedSourceSession.Dispose, which joins exactly these registered tasks.
    private readonly List<(int Target, TaskCompletionSource<bool> Ready)> waiters = [];

    // Hot-path mirror of the LOWEST outstanding target: 0 means "nobody is waiting", so the
    // writer can skip the waiter lock entirely with one volatile read per record. Written only
    // inside the sync lock.
    private volatile int pendingWaitTarget;

    // volatile: read lock-free by IsComplete/readers; written once by the writer thread.
    private volatile bool complete;

    // volatile: written once by the writer thread (inside RunIndexing's catch), read
    // lock-free by IFileIndexer.Failure. Written before `complete` so a reader that
    // observes IsComplete also observes the failure that caused it.
    private volatile IndexFailure? failure;

    /// <summary>
    /// True once the scan has stopped publishing items. For the file indexers this means
    /// "fully indexed"; derived classes with other stop reasons (cancellation, caps)
    /// qualify it with their own flags.
    /// </summary>
    public bool IsComplete => this.complete;

    /// <summary>
    /// Non-null when the scan stopped because of an error; null on success and on
    /// cancellation. See <see cref="RunIndexing"/>.
    /// </summary>
    public IndexFailure? Failure => this.failure;

    /// <summary>
    /// Number of items published so far (may grow until <see cref="IsComplete"/> is true).
    /// </summary>
    public int ItemCount => this.items.Count;

    /// <summary>
    /// Waits (asynchronously) for the writer to reach a target item count. Any number of these
    /// may be outstanding at once, each for its own target; each completes on the first of its
    /// own target being reached or the scan stopping.
    /// </summary>
    /// <param name="targetCount">Number of items that must be published before the task completes</param>
    /// <returns>A task that completes once the scan is complete or the log contains the target number of items</returns>
    protected Task WaitForCountAsync(int targetCount)
    {
        lock (this.sync)
        {
            if (this.items.Count >= targetCount || this.complete)
                return Task.CompletedTask;

            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.waiters.Add((targetCount, ready));
            this.pendingWaitTarget = LowestTarget();

            // Re-check after publishing the flag: the writer may have crossed the target
            // between the first check above and the flag becoming visible to it. The
            // condition is monotone (count only grows), so any later append also notices
            // the flag - this re-check only matters if no further item is ever appended.
            // MarkComplete drains the list under this same lock, so a scan that stopped
            // before the flag went up is caught here rather than left waiting.
            if (this.items.Count >= targetCount || this.complete)
            {
                this.waiters.RemoveAt(this.waiters.Count - 1);
                this.pendingWaitTarget = LowestTarget();
                return Task.CompletedTask;
            }

            return ready.Task;
        }
    }

    /// <summary>The smallest count any outstanding wait is waiting for, or 0 when none is. The
    /// smallest, because the writer's hot-path check is a single comparison and has to fire on
    /// the FIRST target crossed; <see cref="NotifyCountReady"/> then works out which waits that
    /// actually released. Callers hold <see cref="sync"/>.</summary>
    private int LowestTarget()
    {
        int lowest = 0;
        foreach (var (target, _) in this.waiters)
        {
            if (lowest == 0 || target < lowest)
                lowest = target;
        }

        return lowest;
    }

    /// <summary>
    /// Hot-path check the writer runs after publishing items. One volatile read per call
    /// instead of a lock (which would cost 10-20ns per record - a large fraction of the
    /// vectorized scan's per-record budget). A transiently missed flag is harmless: the
    /// condition is monotone, so the next publish re-checks it, and MarkComplete signals
    /// unconditionally at the end of the scan.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void OnItemsPublished(int newCount)
    {
        int waitTarget = this.pendingWaitTarget;
        if (waitTarget != 0 && newCount >= waitTarget)
            this.NotifyCountReady();
    }

    private void NotifyCountReady()
    {
        List<TaskCompletionSource<bool>>? reached = null;
        lock (this.sync)
        {
            int count = this.items.Count;
            for (int i = this.waiters.Count - 1; i >= 0; i--)
            {
                if (this.waiters[i].Target > count)
                    continue;

                (reached ??= []).Add(this.waiters[i].Ready);
                this.waiters.RemoveAt(i);
            }

            this.pendingWaitTarget = LowestTarget();
        }

        // Completed outside the lock: nothing here needs it held, and the waiter machinery is
        // the one thing the writer must never be made to queue behind.
        if (reached is null)
            return;

        foreach (var waiter in reached)
            waiter.TrySetResult(true);
    }

    /// <summary>
    /// Marks the scan as complete and releases EVERY outstanding wait unconditionally - waits
    /// for targets the file never reaches (e.g. an initial-batch wait on a small file, or any
    /// wait outstanding when the scan is cancelled) depend on this signal to complete.
    /// </summary>
    protected void MarkComplete()
    {
        this.complete = true;

        TaskCompletionSource<bool>[] outstanding;
        lock (this.sync)
        {
            outstanding = new TaskCompletionSource<bool>[this.waiters.Count];
            for (int i = 0; i < this.waiters.Count; i++)
                outstanding[i] = this.waiters[i].Ready;

            this.waiters.Clear();
            this.pendingWaitTarget = 0;
        }

        foreach (var waiter in outstanding)
            waiter.TrySetResult(true);
    }

    /// <summary>
    /// Starts <paramref name="body"/> on a background thread as this index's scan, and returns
    /// the task to assign to the derived index's <c>IndexingTask</c>.
    ///
    /// <b>The scan's cancellation token is deliberately NOT passed to <see cref="Task.Run(Action)"/>.</b>
    /// A token already cancelled when the pool picks the work item makes Task.Run skip the body
    /// outright - which would mean <see cref="MarkComplete"/> never runs, <see cref="IsComplete"/>
    /// stays false forever, and every waiter registered through <see cref="WaitForCountAsync"/>
    /// hangs for the life of the process. The body observes cancellation itself and still reaches
    /// <see cref="RunIndexing"/>'s finally, so the completion signal is unconditional. This is not
    /// hypothetical: it deadlocked a document that was disposed between starting its scan and the
    /// pool dequeuing it.
    /// </summary>
    protected Task StartScan(Action body) => Task.Run(() => RunIndexing(body));

    /// <summary><see cref="StartScan"/> for an await-driven scan - see
    /// <see cref="RunIndexingAsync"/>, whose remarks explain why the background hop is required
    /// rather than merely tidy. The same "no token on Task.Run" rule applies, for the same
    /// reason.</summary>
    protected Task StartStreamingScan(Func<Task> body) => Task.Run(() => RunIndexingAsync(body));

    /// <summary>
    /// Runs a scan body on the writer thread, recording any non-cancellation fault as
    /// <see cref="Failure"/> before rethrowing (so <see cref="IFileIndexer.IndexingTask"/>
    /// still faults the same way it always did), and marking the scan complete either way.
    /// Started through <see cref="StartScan"/>, never by a bare Task.Run.
    /// </summary>
    protected void RunIndexing(Action body)
    {
        try
        {
            body();
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a failure
        }
        catch (Exception ex)
        {
            this.failure = DescribeFailure(ex);
            throw;
        }
        finally
        {
            this.MarkComplete();
        }
    }

    /// <summary>
    /// <see cref="RunIndexing"/> for a scan whose body is await-driven rather than CPU-bound -
    /// one that consumes another index as it grows, rather than reading a file itself. Identical
    /// contract: a non-cancellation fault is recorded as <see cref="Failure"/> and rethrown, and
    /// the scan is marked complete either way.
    ///
    /// MUST be started off the UI thread (wrap it in <c>Task.Run</c>). Its awaits resume on
    /// whatever SynchronizationContext was captured, and the app bans
    /// <c>ConfigureAwait(false)</c> (see CLAUDE.md), so a body started on the UI thread would
    /// run a whole background scan in dispatcher turns.
    /// </summary>
    protected async Task RunIndexingAsync(Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            throw; // cancellation is not a failure
        }
        catch (Exception ex)
        {
            this.failure = DescribeFailure(ex);
            throw;
        }
        finally
        {
            this.MarkComplete();
        }
    }

    /// <summary>
    /// Builds the <see cref="IndexFailure"/> recorded for an exception that stopped the
    /// scan. The default reports just the message and how many items were published before
    /// the fault; derived classes override to enrich it (e.g. line/column from a
    /// <see cref="System.Text.Json.JsonException"/>).
    /// </summary>
    protected virtual IndexFailure DescribeFailure(Exception ex) => new(ex.Message, null, null, null, this.ItemCount);
}
