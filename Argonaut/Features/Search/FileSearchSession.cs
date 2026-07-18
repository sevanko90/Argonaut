using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>One search hit: absolute byte offset in the file and the matched byte length.</summary>
public readonly record struct SearchMatch(long Offset, int Length);

/// <summary>
/// One background scan of a memory-mapped file for a search term. Matches stream into a
/// lock-free append log as they're found, so the UI can step through results ("find next")
/// while the scan is still running, exactly the way the file indexers publish their tokens/
/// lines - same single-writer/multi-reader SegmentedAppendLog, same waiter machinery as
/// FileOffsetIndex.
///
/// Knows nothing about JSON structure or the display: it reports byte offsets only. Mapping
/// an offset to a token/line and revealing it is the navigators' job.
///
/// The scan holds spans over the caller's MMapFile, so the file must outlive the scan:
/// callers must <see cref="Cancel"/> and await <see cref="ScanTask"/> before disposing it.
/// </summary>
public sealed class FileSearchSession : IDisposable
{
    private const int DefaultChunkSize = 4 * 1024 * 1024;
    private const int DefaultMaxMatches = 1_000_000;

    // Power of two so the per-match cancellation check is a mask, not a division.
    private const int CancellationCheckInterval = 1024;

    private readonly SegmentedAppendLog<SearchMatch> matches = new();

    // Guards ONLY the cold waiter machinery (registration and completion of matchCountReady).
    // Nothing on the per-match hot path takes this lock - see FileOffsetIndex for the pattern.
    private readonly Lock sync = new();

    private readonly CancellationTokenSource cts = new();

    private TaskCompletionSource<bool>? matchCountReady;
    private int matchCountReadyTarget;

    // Hot-path mirror of matchCountReadyTarget: 0 means "nobody is waiting", so the writer
    // can skip the waiter lock entirely with one volatile read per match. Written only
    // inside the sync lock.
    private volatile int pendingWaitTarget;

    // volatile: read lock-free by IsComplete/readers; written once by the scan thread.
    private volatile bool complete;
    private volatile bool cancelled;
    private volatile bool hitMatchCap;

    private FileSearchSession()
    {
    }

    public Task ScanTask { get; private set; } = Task.CompletedTask;

    /// <summary>Matches found so far (grows until <see cref="IsComplete"/> is true).</summary>
    public int MatchCount => matches.Count;

    /// <summary>True once the scan has stopped - finished, cancelled, or capped.</summary>
    public bool IsComplete => complete;

    /// <summary>True if the scan stopped because <see cref="Cancel"/> was called.</summary>
    public bool WasCancelled => cancelled;

    /// <summary>True if the scan stopped early because it found the maximum number of matches.</summary>
    public bool HitMatchCap => hitMatchCap;

    public SearchMatch GetMatch(int index) => matches.ItemRef(index);

    /// <summary>
    /// Waits (asynchronously) until at least <paramref name="targetCount"/> matches are found,
    /// or the scan stops with fewer than that.
    /// </summary>
    public Task WaitForMatchCountAsync(int targetCount)
    {
        lock (sync)
        {
            if (matches.Count >= targetCount || complete)
                return Task.CompletedTask;

            if (matchCountReady is null || matchCountReady.Task.IsCompleted || targetCount > matchCountReadyTarget)
            {
                matchCountReadyTarget = targetCount;
                matchCountReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            pendingWaitTarget = matchCountReadyTarget;

            // Re-check after publishing the flag: the writer may have crossed the target
            // between the first check above and the flag becoming visible to it. The
            // condition is monotone (count only grows), so any later append also notices
            // the flag - this re-check only matters if no further match is ever appended.
            if (matches.Count >= matchCountReadyTarget || complete)
            {
                var waiter = matchCountReady;
                pendingWaitTarget = 0;
                waiter.TrySetResult(true);
            }

            return matchCountReady.Task;
        }
    }

    /// <summary>
    /// Starts scanning <paramref name="file"/> in the background and returns immediately.
    /// </summary>
    public static FileSearchSession Start(MMapFile file, ISearchMatcher matcher,
        IProgressReporter? progressReporter = null,
        int chunkSize = DefaultChunkSize, int maxMatches = DefaultMaxMatches)
    {
        var session = new FileSearchSession();
        session.ScanTask = Task.Run(() => session.Scan(file, matcher, progressReporter, chunkSize, maxMatches));
        return session;
    }

    /// <summary>
    /// Requests the scan stop. Cooperative: observed between windows and every
    /// <see cref="CancellationCheckInterval"/> matches, so the scan thread lets go of the
    /// file within one window's worth of work. <see cref="ScanTask"/> completes normally
    /// (never faults) on cancellation, releasing any WaitForMatchCountAsync waiter.
    /// </summary>
    public void Cancel() => cts.Cancel();

    // The CTS is deliberately not disposed: Cancel() may race a dispose from another
    // component, and an un-disposed CTS without timers costs nothing beyond GC.
    public void Dispose() => Cancel();

    private void Scan(MMapFile file, ISearchMatcher matcher, IProgressReporter? progressReporter,
        int chunkSize, int maxMatches)
    {
        var ct = cts.Token;
        long length = file.Length;

        try
        {
            if (length == 0)
                return;

            int overlap = matcher.WindowOverlap;

            // The window must comfortably exceed the overlap or the scan can't advance -
            // this also handles a term longer than the configured chunk size.
            int effectiveChunk = (int)Math.Min(int.MaxValue, Math.Max(chunkSize, (long)overlap * 2));

            long windowStart = 0;

            // Dedup cursor: matches are non-overlapping (editor semantics), and re-scanned
            // overlap bytes at a window boundary must not re-emit a match already recorded.
            long searchFrom = 0;

            while (windowStart < length)
            {
                if (ct.IsCancellationRequested)
                    return;

                int size = (int)Math.Min(effectiveChunk, length - windowStart);
                var window = file.GetSpan(windowStart, size);

                int from = (int)(searchFrom - windowStart);
                while (matcher.TryFindNext(window, from, out int matchIndex, out int matchLength))
                {
                    int newCount = matches.Add(new SearchMatch(windowStart + matchIndex, matchLength)) + 1;

                    // One volatile read per match instead of a lock - see FileOffsetIndex.
                    int waitTarget = pendingWaitTarget;
                    if (waitTarget != 0 && newCount >= waitTarget)
                        NotifyMatchCountReady();

                    searchFrom = windowStart + matchIndex + matchLength;
                    from = matchIndex + matchLength;

                    if (newCount >= maxMatches)
                    {
                        hitMatchCap = true;
                        return;
                    }

                    if ((newCount & (CancellationCheckInterval - 1)) == 0 && ct.IsCancellationRequested)
                        return;
                }

                if (windowStart + size >= length)
                    return;

                windowStart += size - overlap;
                searchFrom = Math.Max(searchFrom, windowStart);
                progressReporter?.Report("Searching", windowStart, length);
            }
        }
        finally
        {
            if (ct.IsCancellationRequested)
                cancelled = true;

            MarkComplete();
            progressReporter?.Report("Searching", length, length);
        }
    }

    private void NotifyMatchCountReady()
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (sync)
        {
            if (matchCountReady is not null && !matchCountReady.Task.IsCompleted && matchCountReadyTarget > 0 &&
                matches.Count >= matchCountReadyTarget)
            {
                waiter = matchCountReady;
                pendingWaitTarget = 0;
            }
        }

        waiter?.TrySetResult(true);
    }

    private void MarkComplete()
    {
        complete = true;

        TaskCompletionSource<bool>? waiter;
        lock (sync)
        {
            waiter = matchCountReady;
            pendingWaitTarget = 0;
        }

        waiter?.TrySetResult(true);
    }
}
