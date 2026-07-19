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
/// lines - same single-writer/multi-reader machinery via AppendLogIndexBase.
///
/// Unlike the file indexers, a search session is deliberately independent: it owns its own
/// CancellationTokenSource (exposed as <see cref="Cancel"/>) rather than taking a caller's
/// token, because searches may be started and stopped freely while the file's indexing is
/// still running. For the same reason its IsComplete means "the scan has stopped" - finished,
/// cancelled, or capped (see <see cref="WasCancelled"/>/<see cref="HitMatchCap"/>) - which is
/// why it does not implement IFileIndexer.
///
/// Knows nothing about JSON structure or the display: it reports byte offsets only. Mapping
/// an offset to a token/line and revealing it is the navigators' job.
///
/// The scan holds spans over the caller's MMapFile, so the file must outlive the scan:
/// callers must <see cref="Cancel"/> and await <see cref="ScanTask"/> before disposing it.
/// </summary>
public sealed class FileSearchSession : AppendLogIndexBase<SearchMatch>, IDisposable
{
    private const int DefaultChunkSize = 4 * 1024 * 1024;
    private const int DefaultMaxMatches = 1_000_000;

    // Power of two so the per-match cancellation check is a mask, not a division.
    private const int CancellationCheckInterval = 1024;

    private readonly CancellationTokenSource cts = new();

    // volatile: read lock-free after IsComplete is observed true; written by the scan thread
    // BEFORE MarkComplete's volatile store of the completion flag, so any reader seeing
    // IsComplete also sees these.
    private volatile bool cancelled;
    private volatile bool hitMatchCap;

    private FileSearchSession()
    {
    }

    public Task ScanTask { get; private set; } = Task.CompletedTask;

    /// <summary>Matches found so far (grows until <see cref="AppendLogIndexBase{T}.IsComplete"/> is true).</summary>
    public int MatchCount => this.ItemCount;

    /// <summary>True if the scan stopped because <see cref="Cancel"/> was called.</summary>
    public bool WasCancelled => cancelled;

    /// <summary>True if the scan stopped early because it found the maximum number of matches.</summary>
    public bool HitMatchCap => hitMatchCap;

    public SearchMatch GetMatch(int index) => this.items.ItemRef(index);

    /// <summary>
    /// Waits (asynchronously) until at least <paramref name="targetCount"/> matches are found,
    /// or the scan stops with fewer than that.
    /// </summary>
    public Task WaitForMatchCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

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

            // Chunked-scan loop deliberately duplicated (see also FileOffsetIndex,
            // FileTypeDetector): hot path, indirection would cost more than the shared lines.
            while (windowStart < length)
            {
                if (ct.IsCancellationRequested)
                    return;

                int size = (int)Math.Min(effectiveChunk, length - windowStart);
                var window = file.GetSpan(windowStart, size);

                int from = (int)(searchFrom - windowStart);
                while (matcher.TryFindNext(window, from, out int matchIndex, out int matchLength))
                {
                    int newCount = this.items.Add(new SearchMatch(windowStart + matchIndex, matchLength)) + 1;
                    this.OnItemsPublished(newCount);

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

            this.MarkComplete();
            progressReporter?.Report("Searching", length, length);
        }
    }
}
