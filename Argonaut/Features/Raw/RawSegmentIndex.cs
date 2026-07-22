using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Index for the raw viewer: splits the file into display rows ("segments") that end at either
/// a real '\n' or a forced break at <see cref="WrapWidth"/> bytes, so a file with pathological
/// (or absent) newlines still renders as bounded-length rows.
///
/// Each entry is ONE packed long: bits 0-62 hold the segment's exclusive end offset, bit 63
/// (the sign bit) marks a soft wrap (forced break, the row's content continues on the next
/// row). Segments are contiguous from offset 0 and cover the whole indexed prefix of the file,
/// so a segment's start is simply the previous segment's end and its length never needs
/// storing - it is bounded by <see cref="WrapWidth"/> + 1 by construction.
///
/// Line numbers are not stored per segment either: <see cref="lineStarts"/> records the segment
/// index of each real line's first row, and <see cref="GetLineNumber"/> binary-searches it -
/// an exact hit is a line start (1-based number), a miss is a continuation row.
/// </summary>
public sealed class RawSegmentIndex : AppendLogIndexBase<long>, IFileIndexer
{
    private const long SoftWrapFlag = long.MinValue; // bit 63 - the sign bit, so "flagged" == "negative"
    private const long EndMask = long.MaxValue;      // bits 0-62

    // Size of the window scanned per outer-loop pass; bounds progress-reporting granularity
    // and span length only, nothing is allocated per window (see FileOffsetIndex).
    private const int ScanChunkSize = 4 * 1024 * 1024;

    // A UTF-8 code point is at most 4 bytes, so at most 3 continuation bytes can precede a
    // forced break before the break provably isn't splitting a valid character.
    private const int MaxUtf8Backoff = 3;

    // Ordering contract with AddSegment: each line-start entry is appended BEFORE the segment
    // entry it refers to. The segments log's volatile count publish is a release, so a reader
    // that observes segment i also observes its line-start entry (if it has one).
    private readonly SegmentedAppendLog<int> lineStarts = new();

    // Writer-thread only: whether the next segment begins a real line.
    private bool atLineStart = true;

    private RawSegmentIndex(int wrapWidth)
    {
        WrapWidth = wrapWidth;
    }

    public int WrapWidth { get; }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public string ItemNoun => "rows";

    /// <summary>
    /// Number of display rows indexed so far (may grow until <see cref="AppendLogIndexBase{T}.IsComplete"/> is true).
    /// </summary>
    public int SegmentCount => this.ItemCount;

    /// <summary>Exclusive end offset of the given segment.</summary>
    public long GetSegmentEnd(int segmentIndex) => this.items.ItemRef(segmentIndex) & EndMask;

    /// <summary>Start offset of the given segment - the previous segment's end (segments are contiguous from 0).</summary>
    public long GetSegmentStart(int segmentIndex) => segmentIndex == 0 ? 0 : GetSegmentEnd(segmentIndex - 1);

    /// <summary>True when the segment was force-broken at the wrap width (its content continues on the next row).</summary>
    public bool IsSoftWrapped(int segmentIndex) => this.items.ItemRef(segmentIndex) < 0;

    /// <summary>
    /// 1-based line number when the segment is the first row of a real line; null for a
    /// continuation row. Valid mid-indexing: line starts are appended in ascending segment
    /// order and published before their segment (see <see cref="lineStarts"/>).
    /// </summary>
    public int? GetLineNumber(int segmentIndex)
    {
        int lo = 0, hi = this.lineStarts.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            int startSegment = this.lineStarts.ItemRef(mid);
            if (startSegment == segmentIndex)
                return mid + 1;

            if (startSegment < segmentIndex)
                lo = mid + 1;
            else
                hi = mid - 1;
        }

        return null;
    }

    /// <summary>
    /// Waits (asynchronously) for the indexer to reach a target segment count
    /// </summary>
    /// <param name="targetCount">number of segments that must be indexed before the task completes</param>
    /// <returns>A task that completes once the index is complete or contains the target number of segments</returns>
    public Task WaitForSegmentCountAsync(int targetCount) => this.WaitForCountAsync(targetCount);

    /// <summary>
    /// Start the process of indexing the file and return the index, initially populating in the background.
    /// </summary>
    /// <param name="file">Memory mapped file to index</param>
    /// <param name="wrapWidth">Byte cap per display row; rows are force-broken at this length</param>
    /// <param name="progressReporter">Progress reporter</param>
    /// <param name="cancellationToken">Checked once per scan chunk - see <see cref="FileOffsetIndex"/></param>
    public static RawSegmentIndex StartIndexing(MMapFile file, int wrapWidth, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wrapWidth, MaxUtf8Backoff + 1);

        var index = new RawSegmentIndex(wrapWidth);
        index.IndexingTask = Task.Run(() => index.ProduceSegments(file, progressReporter, cancellationToken), cancellationToken);
        return index;
    }

    /// <summary>
    /// Scans the file and appends one packed entry per display row. Chunked-scan loop
    /// deliberately duplicated from FileOffsetIndex (hot path; see the note there), with the
    /// newline search additionally capped at the bytes remaining before a forced break.
    /// </summary>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceSegments(MMapFile file, IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        long length = file.Length;
        if (length == 0)
        {
            this.MarkComplete();
            progressReporter?.Report("Indexing");
            return;
        }

        long scanCursor = 0;
        long segmentStart = 0;
        try
        {
            while (scanCursor < length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long chunkBase = scanCursor;
                int size = (int)Math.Min(ScanChunkSize, length - chunkBase);
                var chunk = file.GetSpan(chunkBase, size);

                int pos = 0;
                while (pos < size)
                {
                    // Bytes the current segment may still grow by before hitting the cap.
                    // Invariant at this point: scan position < segmentStart + WrapWidth
                    // (every cap hit emits a segment immediately), so this is >= 1.
                    int capRemaining = (int)(segmentStart + WrapWidth - (chunkBase + pos));
                    int searchLength = Math.Min(size - pos, capRemaining);

                    int newlineIndex = chunk.Slice(pos, searchLength).IndexOf((byte)'\n');
                    if (newlineIndex >= 0)
                    {
                        long end = chunkBase + pos + newlineIndex + 1;
                        AddSegment(end, softWrap: false);
                        segmentStart = end;
                        pos = (int)(end - chunkBase);
                    }
                    else if (capRemaining <= size - pos)
                    {
                        long end = BreakAtCap(file, segmentStart, length, out bool softWrap);
                        AddSegment(end, softWrap);
                        segmentStart = end;
                        // A newline peek-extension can push the end one byte past this chunk;
                        // pos then exceeds size, the inner loop exits, and the next chunk is
                        // based at the segment end.
                        pos = (int)(end - chunkBase);
                    }
                    else
                    {
                        // Chunk exhausted mid-segment (no newline, cap not yet reached) - the
                        // segment carries into the next chunk.
                        pos = size;
                    }
                }

                scanCursor = chunkBase + pos;
                progressReporter?.Report("Indexing", Math.Min(scanCursor, length), length);
            }
        }
        finally
        {
            // Only on a genuine end-of-file is there a trailing segment to record. On
            // cancellation segmentStart is wherever the scan stopped, and the remainder can
            // exceed any per-row bound - skip it (see FileOffsetIndex for the same trap).
            if (!cancellationToken.IsCancellationRequested && segmentStart < length)
            {
                AddSegment(length, softWrap: false);
            }

            this.MarkComplete();
            progressReporter?.Report("Indexing", length, length);
        }
    }

    /// <summary>
    /// Chooses where a cap-bounded segment starting at <paramref name="segmentStart"/> ends.
    /// Peeks one byte past the cap first: a '\n' exactly at the cap is pulled into the segment
    /// as a real line end, so a CRLF straddling the cap can't leave a lone '\n' row. Otherwise
    /// backs the break off up to 3 bytes so a multi-byte UTF-8 character isn't split; if every
    /// candidate is a continuation byte (binary data), breaks at the cap regardless.
    /// </summary>
    private long BreakAtCap(MMapFile file, long segmentStart, long length, out bool softWrap)
    {
        long capEnd = segmentStart + WrapWidth;
        if (capEnd >= length)
        {
            // The cap lands exactly on end-of-file: nothing continues, so it's a real end.
            softWrap = false;
            return length;
        }

        if (file.GetSpan(capEnd, 1)[0] == (byte)'\n')
        {
            softWrap = false;
            return capEnd + 1;
        }

        softWrap = true;
        long end = capEnd;
        for (int back = 0; back < MaxUtf8Backoff && end - 1 > segmentStart; back++)
        {
            if (!IsUtf8ContinuationByte(file.GetSpan(end, 1)[0]))
                return end;

            end--;
        }

        return IsUtf8ContinuationByte(file.GetSpan(end, 1)[0]) ? capEnd : end;
    }

    private static bool IsUtf8ContinuationByte(byte b) => (b & 0xC0) == 0x80;

    private void AddSegment(long endExclusive, bool softWrap)
    {
        // Line start goes in BEFORE the segment publish - see the lineStarts ordering note.
        if (this.atLineStart)
            this.lineStarts.Add(this.items.Count);
        this.atLineStart = !softWrap;

        int newCount = this.items.Add(softWrap ? endExclusive | SoftWrapFlag : endExclusive) + 1;
        this.OnItemsPublished(newCount);
    }
}
