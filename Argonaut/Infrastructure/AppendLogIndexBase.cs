using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Argonaut.Infrastructure;

/// <summary>
/// Shared base for the background scanners that publish fixed-size records into a
/// <see cref="SegmentedAppendLog{T}"/> from a single writer thread while UI-thread readers
/// consume them lock-free (FileOffsetIndex, JsonStructureIndex, FileSearchSession).
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

    // Guards ONLY the cold waiter machinery below (registration and completion of
    // countReady). Nothing on the per-record hot path takes this lock.
    private readonly Lock sync = new();

    private TaskCompletionSource<bool>? countReady;
    private int countReadyTarget;

    // Hot-path mirror of countReadyTarget: 0 means "nobody is waiting", so the writer
    // can skip the waiter lock entirely with one volatile read per record. Written only
    // inside the sync lock.
    private volatile int pendingWaitTarget;

    // volatile: read lock-free by IsComplete/readers; written once by the writer thread.
    private volatile bool complete;

    /// <summary>
    /// True once the scan has stopped publishing items. For the file indexers this means
    /// "fully indexed"; derived classes with other stop reasons (cancellation, caps)
    /// qualify it with their own flags.
    /// </summary>
    public bool IsComplete => this.complete;

    /// <summary>
    /// Number of items published so far (may grow until <see cref="IsComplete"/> is true).
    /// </summary>
    public int ItemCount => this.items.Count;

    /// <summary>
    /// Waits (asynchronously) for the writer to reach a target item count.
    /// </summary>
    /// <param name="targetCount">Number of items that must be published before the task completes</param>
    /// <returns>A task that completes once the scan is complete or the log contains the target number of items</returns>
    protected Task WaitForCountAsync(int targetCount)
    {
        lock (this.sync)
        {
            if (this.items.Count >= targetCount || this.complete)
                return Task.CompletedTask;

            if (this.countReady is null || this.countReady.Task.IsCompleted || targetCount > this.countReadyTarget)
            {
                this.countReadyTarget = targetCount;
                this.countReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            this.pendingWaitTarget = this.countReadyTarget;

            // Re-check after publishing the flag: the writer may have crossed the target
            // between the first check above and the flag becoming visible to it. The
            // condition is monotone (count only grows), so any later append also notices
            // the flag - this re-check only matters if no further item is ever appended.
            if (this.items.Count >= this.countReadyTarget || this.complete)
            {
                var waiter = this.countReady;
                this.pendingWaitTarget = 0;
                waiter.TrySetResult(true);
            }

            return this.countReady.Task;
        }
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
        TaskCompletionSource<bool>? waiter = null;
        lock (this.sync)
        {
            if (this.countReady is not null && !this.countReady.Task.IsCompleted && this.countReadyTarget > 0 &&
                this.items.Count >= this.countReadyTarget)
            {
                waiter = this.countReady;
                this.pendingWaitTarget = 0;
            }
        }

        waiter?.TrySetResult(true);
    }

    /// <summary>
    /// Marks the scan as complete and releases any waiter unconditionally - waits for
    /// targets the file never reaches (e.g. an initial-batch wait on a small file) depend
    /// on this signal to complete.
    /// </summary>
    protected void MarkComplete()
    {
        this.complete = true;

        TaskCompletionSource<bool>? waiter;
        lock (this.sync)
        {
            waiter = this.countReady;
            this.pendingWaitTarget = 0;
        }

        waiter?.TrySetResult(true);
    }
}
