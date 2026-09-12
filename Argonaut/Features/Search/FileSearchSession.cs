using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Search;

/// <summary>One search hit: absolute byte offset in the file and the matched byte length.</summary>
public readonly record struct SearchMatch(long Offset, int Length);

/// <summary>
/// Where a scan reads from: a whole file, or one byte range of it. <see cref="Length"/> below
/// zero means "the whole file", resolved from <see cref="FileInfo"/> when the scan starts.
///
/// A range target exists so a sub-document (one NDJSON line, viewed through its own zero-based
/// mapping) can be searched in the SAME coordinate system its index uses: reported offsets are
/// always relative to <see cref="Offset"/>, never absolute in the file. Handing the engine a
/// bare path instead would silently scan the whole parent file and report offsets the
/// sub-document's index cannot resolve.
/// </summary>
public readonly record struct ScanTarget(string Path, long Offset = 0, long Length = -1);

/// <summary>
/// One background scan of a file for a search term. Matches stream into a lock-free append log
/// as they're found, so the UI can step through results ("find next") while the scan is still
/// running, exactly the way the file indexers publish their tokens/lines - same
/// single-writer/multi-reader machinery via AppendLogIndexBase.
///
/// Unlike the file indexers, a search session is deliberately independent: it owns its own
/// CancellationTokenSource (exposed as <see cref="RequestStop"/>) rather than taking a caller's
/// token, because searches may be started and stopped freely while the file's indexing is
/// still running. For the same reason its IsComplete means "the scan has stopped" - finished,
/// cancelled, capped, or unable to read the file (see <see cref="WasCancelled"/>/
/// <see cref="HitMatchCap"/>/<see cref="OpenFailure"/>) - which is why it does not implement
/// IFileIndexer.
///
/// Knows nothing about JSON structure or the display: it reports byte offsets only. Mapping
/// an offset to a token/line and revealing it is the navigators' job.
///
/// The scan opens its OWN mapping of the target, one chunk at a time. Nothing outside this
/// class owns memory it reads, so a document can be torn down while a scan over the same path
/// runs, and stopping a scan is never a precondition for releasing anything.
/// </summary>
public sealed class FileSearchSession : AppendLogIndexBase<SearchMatch>, IMatchSource, IDisposable
{
    private const int DefaultChunkSize = 4 * 1024 * 1024;
    private const int DefaultMaxMatches = 1_000_000;

    // Power of two so the per-match cancellation check is a mask, not a division.
    private const int CancellationCheckInterval = 1024;

    private readonly CancellationTokenSource stopSource = new();

    // volatile: read lock-free after IsComplete is observed true; written by the scan thread
    // BEFORE MarkComplete's volatile store of the completion flag, so any reader seeing
    // IsComplete also sees these.
    private volatile bool cancelled;
    private volatile bool hitMatchCap;
    private volatile string? openFailure;

    private FileSearchSession()
    {
    }

    public Task ScanTask { get; private set; } = Task.CompletedTask;

    /// <summary>Matches found so far (grows until <see cref="AppendLogIndexBase{T}.IsComplete"/> is true).</summary>
    public int MatchCount => this.ItemCount;

    /// <summary>True if the scan stopped because <see cref="RequestStop"/> was called.</summary>
    public bool WasCancelled => cancelled;

    /// <summary>True if the scan stopped early because it found the maximum number of matches.</summary>
    public bool HitMatchCap => hitMatchCap;

    /// <summary>
    /// Why the scan stopped being able to read the file, or null when it ran to the end. Since
    /// the scan opens the file itself - once per chunk - an unreadable target is an OUTCOME
    /// rather than a throw out of <see cref="Start"/>, and can happen partway through. Matches
    /// found before the failure are kept.
    /// </summary>
    public string? OpenFailure => openFailure;

    public SearchMatch GetMatch(int index) => this.items.ItemRef(index);

    /// <summary>
    /// Waits (asynchronously) until at least <paramref name="targetCount"/> matches are found,
    /// or the scan stops with fewer than that.
    /// </summary>
    public Task WaitForMatchCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    /// <summary>
    /// Starts scanning <paramref name="target"/> in the background and returns immediately.
    /// Never throws for an unreadable target - see <see cref="OpenFailure"/>.
    /// </summary>
    public static FileSearchSession Start(ScanTarget target, ISearchMatcher matcher,
        IProgressReporter? progressReporter = null,
        int chunkSize = DefaultChunkSize, int maxMatches = DefaultMaxMatches)
    {
        var session = new FileSearchSession();
        session.ScanTask = Task.Run(() => session.Scan(target, matcher, progressReporter, chunkSize, maxMatches));
        return session;
    }

    /// <summary>
    /// Asks the scan to stop. Cooperative: observed between chunks and every
    /// <see cref="CancellationCheckInterval"/> matches, so the scan thread stops within one
    /// chunk's worth of work. Returns immediately and joins nothing; <see cref="ScanTask"/>
    /// completes normally (never faults), releasing any WaitForMatchCountAsync waiter.
    /// Same verb, same contract as <see cref="IDocumentSession.RequestStop"/>.
    /// </summary>
    public void RequestStop() => stopSource.Cancel();

    // stopSource is deliberately not disposed: RequestStop() may race a dispose from another
    // component, and an un-disposed CTS without timers costs nothing beyond GC.
    public void Dispose() => RequestStop();

    private void Scan(ScanTarget target, ISearchMatcher matcher, IProgressReporter? progressReporter,
        int chunkSize, int maxMatches)
    {
        var ct = stopSource.Token;
        long length = 0;

        try
        {
            // The real data length, from the OS, before anything is mapped - never an
            // accessor capacity (see CLAUDE.md).
            length = target.Length < 0 ? new FileInfo(target.Path).Length : target.Length;
            if (length == 0)
                return;

            int overlap = matcher.ChunkOverlap;

            // The chunk must comfortably exceed the overlap or the scan can't advance -
            // this also handles a term longer than the configured chunk size.
            int effectiveChunk = (int)Math.Min(int.MaxValue, Math.Max(chunkSize, (long)overlap * 2));

            long chunkStart = 0;

            // Dedup cursor: matches are non-overlapping (editor semantics), and re-scanned
            // overlap bytes at a chunk boundary must not re-emit a match already recorded.
            long searchFrom = 0;

            // Chunked-scan loop deliberately duplicated (see also FileOffsetIndex,
            // FileTypeDetector): hot path, indirection would cost more than the shared lines.
            while (chunkStart < length)
            {
                if (ct.IsCancellationRequested)
                    return;

                int size = (int)Math.Min(effectiveChunk, length - chunkStart);

                // A mapping per iteration is deliberate, not an oversight. Mapping the whole
                // file (what the indexers do) would leave every page this scan touches resident
                // in a SECOND mapping beside the document's own: double-counted in RSS, and a
                // multi-GB unmap at the end contending for the process-wide address-space lock
                // with the document's own unmap on the UI thread at close. One chunk caps both,
                // for tens of microseconds against ~1ms of scanning per chunk.
                using (var view = new MMapFile(target.Path, target.Offset + chunkStart, size))
                {
                    var chunk = view.RequireContiguous(0, size);

                    int from = (int)(searchFrom - chunkStart);
                    while (matcher.TryFindNext(chunk, from, out int matchIndex, out int matchLength))
                    {
                        int newCount = this.items.Add(new SearchMatch(chunkStart + matchIndex, matchLength)) + 1;
                        this.OnItemsPublished(newCount);

                        searchFrom = chunkStart + matchIndex + matchLength;
                        from = matchIndex + matchLength;

                        if (newCount >= maxMatches)
                        {
                            hitMatchCap = true;
                            return;
                        }

                        if ((newCount & (CancellationCheckInterval - 1)) == 0 && ct.IsCancellationRequested)
                            return;
                    }
                }

                if (chunkStart + size >= length)
                    return;

                chunkStart += size - overlap;
                searchFrom = Math.Max(searchFrom, chunkStart);
                progressReporter?.Report("Searching", chunkStart, length);
            }
        }
        catch (Exception ex)
        {
            // The scan opens the file itself, so an unreadable/vanished/locked target is an
            // outcome rather than a fault. Caught wholesale so ScanTask NEVER faults: nothing
            // joins it any more (FindController cancels and forgets), and an unobserved
            // faulted task would surface at finalization as an UnobservedTaskException.
            openFailure = ex.Message;
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
