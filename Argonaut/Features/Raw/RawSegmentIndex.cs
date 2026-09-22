using System;
using System.Threading;
using System.Threading.Tasks;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Everything the viewer needs to display one row: its byte range, whether it was force-broken
/// at the wrap cap (drives the ⏎ gutter), and its 1-based line number - null on continuation
/// rows, whose left gutter stays blank.
/// </summary>
public readonly record struct RawRowInfo(long Start, long End, bool IsSoftWrapped, int? LineNumber);

/// <summary>
/// One sparse index entry: the full position state at the start of an anchor row. Unlike a
/// dense per-row index, neither the byte offset nor the line number is derivable from the
/// entry's index, so both are stored: bits 0-60 of <see cref="PackedOffset"/> are the anchor
/// row's start offset, bits 61-62 how far that start was backed off from its cap (0 on a line
/// start - it is what puts a resumed walk back on the line's cap grid, see
/// <see cref="RawRowBoundary"/>), bit 63 (the sign bit) marks a continuation row (the anchor row
/// does NOT start a real line), and <see cref="LineNumber"/> is the 1-based number of the line
/// containing the anchor row.
/// </summary>
public readonly record struct RawRowAnchor(long PackedOffset, int LineNumber);

/// <summary>
/// Sparse index for the raw viewer: the file is segmented into display rows ("segments") that
/// end at either a real '\n' or a forced break at <see cref="WrapWidth"/> bytes, but only
/// every <see cref="AnchorStride"/>th row boundary is stored. The rows in between are
/// re-derived on demand by re-running the (deterministic, byte-driven) segmentation rules
/// forward from the preceding anchor - a bounded rescan of at most
/// AnchorStride × (WrapWidth + 4) mapped bytes, which is what keeps the index at ~16 bytes
/// per 64 rows (a dense per-row index on a multi-GB file runs to hundreds of MB).
///
/// Anchoring by display row rather than by line is what makes pathological lines free: a
/// 100MB line is thousands of rows, each bucket of 64 still spanning only ~64 × cap bytes,
/// so no dense/sparse switching is ever needed.
///
/// The scan and the on-demand rescan share one boundary implementation
/// (<see cref="RawRowCursor"/>), so they cannot disagree. <see cref="RowCount"/> is
/// published at anchor boundaries (and finally at completion), guaranteeing every published
/// row's bucket anchor is already visible; the base class's item (= anchor) waiter machinery
/// underpins <see cref="WaitForRowCountAsync"/>.
/// </summary>
public sealed class RawSegmentIndex : AppendLogIndexBase<RawRowAnchor>, IBackgroundIndex, IRawRowIndex
{
    /// <summary>Rows per stored anchor. The RAM/rescan trade: 16 bytes per stride rows of
    /// index, at most stride × (WrapWidth + 4) bytes rescanned per row lookup.</summary>
    internal const int AnchorStride = 64;

    private const long ContinuationFlag = long.MinValue; // bit 63 - the sign bit
    private const int BackoffShift = 61;                 // bits 61-62
    private const long OffsetMask = (1L << BackoffShift) - 1;

    /// <summary>Bytes scanned between progress reports: often enough that a multi-GB scan moves
    /// smoothly, rarely enough that reporting never shows in the scan's profile.</summary>
    private const long ProgressReportStride = 4 * 1024 * 1024;
    private const int CancellationCheckRowStride = 1024;

    private readonly IByteSource source;

    // Rows whose boundaries are fully determined AND whose bucket anchor is published.
    // Written by the scan thread (release via Volatile.Write), read lock-free by the UI.
    private int publishedRowCount;

    private RawSegmentIndex(IByteSource source, int wrapWidth)
    {
        this.source = source;
        WrapWidth = wrapWidth;
    }

    public int WrapWidth { get; }

    public Task IndexingTask { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Number of display rows available so far (may grow, in anchor-stride steps, until
    /// <see cref="AppendLogIndexBase{T}.AllItemsPublished"/> is true).
    /// </summary>
    public int RowCount => Volatile.Read(ref this.publishedRowCount);

    /// <summary>
    /// Resolves one display row by walking the segmentation rules forward from the row's
    /// bucket anchor: at most <see cref="AnchorStride"/> boundary computations over mapped
    /// bytes, no allocation.
    /// </summary>
    public RawRowInfo GetRowInfo(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
            throw new ArgumentOutOfRangeException(nameof(rowIndex));

        int anchorIndex = rowIndex / AnchorStride;
        var cursor = AnchorAt(anchorIndex);
        for (int row = anchorIndex * AnchorStride; ; row++)
        {
            var before = cursor;
            var (end, softWrap) = cursor.Advance(this.source, WrapWidth);
            if (row == rowIndex)
                return new RawRowInfo(before.Start, end, softWrap, before.AtLineStart ? before.LineNumber : null);
        }
    }

    /// <summary>
    /// See <see cref="IRawRowIndex.LineContaining"/>. The same walk <see cref="GetRowInfo"/> does,
    /// reporting the line number it tracks on the way rather than dropping it on a continuation
    /// row: at most <see cref="AnchorStride"/> boundary computations, and nothing stored per row.
    /// </summary>
    public int? LineContaining(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
            return null;

        int anchorIndex = rowIndex / AnchorStride;
        var cursor = AnchorAt(anchorIndex);
        for (int row = anchorIndex * AnchorStride; row < rowIndex; row++)
            cursor.Advance(this.source, WrapWidth);

        return cursor.LineNumber;
    }

    /// <summary>
    /// The full position state stored at anchor <paramref name="anchorIndex"/>, as a cursor a walk
    /// can resume from: where its row starts, the cap its row ends against, whether that row begins
    /// a real line, and the line it sits in. <see cref="GetRowInfo"/> cannot answer this - it
    /// reports a null line number on a continuation row, which is right for a gutter but useless to
    /// a caller that needs to keep counting lines forward from there.
    /// </summary>
    internal RawRowCursor AnchorAt(int anchorIndex)
    {
        var anchor = this.items.ItemRef(anchorIndex);
        long start = anchor.PackedOffset & OffsetMask;
        int backoff = (int)((anchor.PackedOffset >> BackoffShift) & 3);
        return new RawRowCursor
        {
            Start = start,
            NextCap = start + backoff + WrapWidth,
            AtLineStart = anchor.PackedOffset >= 0,
            LineNumber = anchor.LineNumber,
        };
    }

    /// <summary>Anchors stored so far. Every published row's anchor is among them.</summary>
    internal int AnchorCount => (RowCount + AnchorStride - 1) / AnchorStride;

    /// <summary>
    /// Maps an absolute byte offset to the display row containing it: binary search over the
    /// anchors, then a single forward walk through the bucket. Returns null when the offset
    /// isn't covered by the published rows (yet).
    /// </summary>
    public int? RowForOffset(long offset)
    {
        int rowCount = RowCount;
        if (rowCount == 0 || offset < 0)
            return null;

        // Only anchors whose bucket has published rows participate - when indexing is mid-file
        // the newest anchor's bucket is still empty (RowCount stops at that anchor's row).
        int coveredAnchors = (rowCount + AnchorStride - 1) / AnchorStride;

        int anchorIndex = AnchorAtOrBefore(offset, coveredAnchors);
        var cursor = AnchorAt(anchorIndex);
        int lastRow = Math.Min(rowCount, (anchorIndex + 1) * AnchorStride) - 1;
        for (int row = anchorIndex * AnchorStride; row <= lastRow; row++)
        {
            if (offset < cursor.Advance(this.source, WrapWidth).End)
                return row;
        }

        return null; // beyond the published rows
    }

    /// <summary>
    /// Where the line holding <paramref name="offset"/> begins: its start offset, its first row
    /// and its number. What <see cref="RawEditedRowIndex"/> needs to open a run of whole lines
    /// around an edit without walking the line.
    ///
    /// One bucket walk when the line begins inside the bucket holding the offset - every line
    /// shorter than a few rows. A longer line began in an earlier bucket, and the anchors carry
    /// line numbers, so a binary search finds the first anchor in that line and at most one more
    /// bucket walk reaches its start: O(log anchors) either way, however long the line.
    ///
    /// At or past the end of the data this is the phantom empty line after a trailing '\n' (or of
    /// an empty source) - no rows, starting at the end - and otherwise the last line. Requires a
    /// complete scan.
    /// </summary>
    internal (long Start, int FirstRow, int LineNumber) LineStartContaining(long offset)
    {
        int rowCount = RowCount;
        if (rowCount == 0)
            return (0, 0, 1);

        long length = this.source.AvailableLength;
        if (offset >= length)
        {
            if (RawRowBoundary.ByteAt(this.source, length - 1) == (byte)'\n')
                return (length, rowCount, LineContaining(rowCount - 1)!.Value + 1);

            offset = length - 1;
        }

        int coveredAnchors = AnchorCount;
        int anchorIndex = AnchorAtOrBefore(Math.Max(offset, 0), coveredAnchors);
        var cursor = AnchorAt(anchorIndex);
        int row = anchorIndex * AnchorStride;
        long lineStart = -1;
        int lineStartRow = 0;
        int line;
        while (true)
        {
            var at = cursor;
            if (at.AtLineStart)
            {
                lineStart = at.Start;
                lineStartRow = row;
            }

            if (offset < cursor.Advance(this.source, WrapWidth).End)
            {
                line = at.LineNumber;
                break;
            }

            row++;
        }

        if (lineStart >= 0)
            return (lineStart, lineStartRow, line);

        // The line began before this bucket. Anchors are in line order, and the first one in this
        // line is either its first row or a continuation - in which case the line began in the
        // bucket before it.
        int first = FirstAnchorInLineAtLeast(line, coveredAnchors);
        cursor = AnchorAt(first);
        if (cursor.AtLineStart)
            return (cursor.Start, first * AnchorStride, line);

        cursor = AnchorAt(first - 1);
        row = (first - 1) * AnchorStride;
        while (!(cursor.AtLineStart && cursor.LineNumber == line))
        {
            cursor.Advance(this.source, WrapWidth);
            row++;
        }

        return (cursor.Start, row, line);
    }

    /// <summary>
    /// Where the line holding <paramref name="offset"/> ends: the exclusive end of its last row
    /// (the '\n' included), whether it has a '\n' at all, the row after its last, and its number.
    /// The counterpart of <see cref="LineStartContaining"/>, bounded the same way: the walk that
    /// finds the offset carries on to the line's end, and a line that outruns the bucket is ended
    /// by binary-searching for the first anchor in a later line and walking the bucket before it.
    /// Same end-of-data behaviour as <see cref="LineStartContaining"/>. Requires a complete scan.
    /// </summary>
    internal (long End, bool Terminated, int RowsEnd, int LineNumber) LineEndContaining(long offset)
    {
        int rowCount = RowCount;
        if (rowCount == 0)
            return (0, false, 0, 1);

        long length = this.source.AvailableLength;
        if (offset >= length)
        {
            if (RawRowBoundary.ByteAt(this.source, length - 1) == (byte)'\n')
                return (length, false, rowCount, LineContaining(rowCount - 1)!.Value + 1);

            offset = length - 1;
        }

        int coveredAnchors = AnchorCount;
        int anchorIndex = AnchorAtOrBefore(Math.Max(offset, 0), coveredAnchors);
        var cursor = AnchorAt(anchorIndex);
        int row = anchorIndex * AnchorStride;
        int bucketEnd = Math.Min(rowCount, (anchorIndex + 1) * AnchorStride);
        int line = 0;
        bool found = false;
        for (; row < bucketEnd; row++)
        {
            var at = cursor;
            var (end, softWrap) = cursor.Advance(this.source, WrapWidth);
            if (!found && offset < end)
            {
                found = true;
                line = at.LineNumber;
            }

            if (found && !softWrap)
                return (end, EndsInNewline(end), row + 1, line);
        }

        // The line outran the bucket. It ends in the bucket before the first anchor of a later
        // line - or in the last bucket, when it is the last line.
        int next = FirstAnchorInLineAtLeast(line + 1, coveredAnchors);
        cursor = AnchorAt(next - 1);
        row = (next - 1) * AnchorStride;
        while (true)
        {
            var at = cursor;
            var (end, softWrap) = cursor.Advance(this.source, WrapWidth);
            if (!softWrap && at.LineNumber == line)
                return (end, EndsInNewline(end), row + 1, line);

            row++;
        }
    }

    private bool EndsInNewline(long end) => end > 0 && RawRowBoundary.ByteAt(this.source, end - 1) == (byte)'\n';

    /// <summary>Greatest of the first <paramref name="coveredAnchors"/> anchors starting at or
    /// before <paramref name="offset"/>.</summary>
    private int AnchorAtOrBefore(long offset, int coveredAnchors)
    {
        int lo = 0, hi = coveredAnchors - 1, found = 0;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if ((this.items.ItemRef(mid).PackedOffset & OffsetMask) <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>First anchor whose row sits in line <paramref name="line"/> or later, or
    /// <paramref name="coveredAnchors"/> when there is none.</summary>
    private int FirstAnchorInLineAtLeast(int line, int coveredAnchors)
    {
        int lo = 0, hi = coveredAnchors - 1, found = coveredAnchors;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (this.items.ItemRef(mid).LineNumber >= line)
            {
                found = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found;
    }

    /// <summary>Exclusive end offset of the last published row; 0 when nothing is published.
    /// The coverage watermark for offset-based waits (see RawOffsetRowResolver).</summary>
    internal long PublishedEndOffset
    {
        get
        {
            int rowCount = RowCount;
            return rowCount == 0 ? 0 : GetRowInfo(rowCount - 1).End;
        }
    }

    /// <summary>
    /// Waits (asynchronously) for the indexer to publish a target row count. Waits on the
    /// anchor count that implies the row target (anchors c publish rows (c-1) × stride, so
    /// ceil(target/stride) + 1 anchors guarantee it), NOT one-anchor-at-a-time: a caller
    /// waiting for a far target against a multi-GB scan would otherwise be woken - allocating
    /// a waiter task each time - once per anchor, hundreds of thousands of times. Completion
    /// releases the wait regardless of the target; the loop is a safety re-check only.
    /// </summary>
    public async Task WaitForRowCountAsync(int targetCount)
    {
        while (RowCount < targetCount && !AllItemsPublished)
            await WaitForCountAsync((targetCount + AnchorStride - 1) / AnchorStride + 1);
    }

    /// <summary>
    /// Start the process of indexing the file and return the index, initially populating in the background.
    /// </summary>
    /// <param name="source">Bytes to index; must outlive this index (the on-demand row rescans
    /// read it for the index's whole lifetime - see RawIndexSession). A plain mapping while the
    /// document is unedited, a piece table over (mapping, scratch) once it is not.</param>
    /// <param name="wrapWidth">Byte cap per display row; rows are force-broken at this length</param>
    /// <param name="progressReporter">Progress reporter</param>
    /// <param name="cancellationToken">Checked every <see cref="CancellationCheckRowStride"/> rows</param>
    public static RawSegmentIndex StartIndexing(IByteSource source, int wrapWidth, IProgressReporter? progressReporter = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wrapWidth, RawRowBoundary.MaxUtf8Backoff + 1);

        var index = new RawSegmentIndex(source, wrapWidth);
        index.IndexingTask = index.StartScan(() => index.ProduceRows(progressReporter, cancellationToken));
        return index;
    }

    /// <summary>
    /// Scans the file row by row, storing an anchor every <see cref="AnchorStride"/> rows.
    /// Per-row rather than chunked like FileOffsetIndex: the newline search is still the
    /// SIMD-vectorized span IndexOf, capped at the wrap width, and sharing
    /// <see cref="RawRowCursor"/> with the on-demand rescan is what guarantees the two always
    /// agree on where rows fall.
    /// </summary>
    /// <remarks>Invoked in the background via a task</remarks>
    private void ProduceRows(IProgressReporter? progressReporter, CancellationToken cancellationToken)
    {
        var cursor = RawRowCursor.StartOfLine(0, 1, WrapWidth);
        int rows = 0;
        long nextProgressReport = ProgressReportStride;

        // Cached, not snapshotted: this loop runs once per row rather than once per chunk, so
        // asking the source how much has arrived on every row would add two interface calls per
        // row to a scan that does tens of millions of them. Instead the pair is cached and
        // refreshed only when a row actually reaches the cached end - which for a settled source
        // (every source but a streamed one) happens exactly once, at the end of the data.
        long available = this.source.AvailableLength;
        bool settled = this.source.LengthSettled;

        try
        {
            while (true)
            {
                if (rows % CancellationCheckRowStride == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                if (cursor.Start >= available)
                {
                    available = this.source.AvailableLength;
                    settled = this.source.LengthSettled;
                    if (cursor.Start >= available)
                    {
                        if (settled)
                            break;

                        this.source.WaitForLength(cursor.Start + 1, cancellationToken);
                        continue;
                    }
                }

                // Advanced on a copy: a step abandoned below to wait for more data must leave the
                // cursor where it was.
                var next = cursor;
                long end = next.Advance(this.source, WrapWidth).End;

                // A row that ends exactly where the data currently does may only look finished
                // because the rest has not arrived - its newline could be the next byte, or it
                // could wrap further. This index is append-only with bucket anchors, so a row
                // published at the wrong width can never be corrected: wait for more instead.
                if (end >= available && !settled)
                {
                    available = this.source.AvailableLength;
                    settled = this.source.LengthSettled;
                    if (end >= available && !settled)
                    {
                        this.source.WaitForLength(available + 1, cancellationToken);
                        continue;
                    }
                }

                if (rows % AnchorStride == 0)
                    AppendAnchor(cursor, rows);

                rows++;
                cursor = next;
                if (cursor.Start >= nextProgressReport)
                {
                    progressReporter?.Report("Indexing", cursor.Start, available);
                    nextProgressReport = cursor.Start + ProgressReportStride;
                }
            }
        }
        finally
        {
            // Every counted row is fully determined and its bucket anchor is already stored
            // (the anchor goes in before its bucket's first row is scanned), so the final
            // count is safe to publish even on cancellation - unlike the dense indexers,
            // there is no un-scanned remainder to mis-record.
            Volatile.Write(ref this.publishedRowCount, rows);
            progressReporter?.Report("Indexing", cursor.Start, cursor.Start);
        }
    }

    private void AppendAnchor(RawRowCursor cursor, int rowsSoFar)
    {
        long packed = cursor.Start | ((long)cursor.BackoffFromCap(WrapWidth) << BackoffShift);
        this.items.Add(new RawRowAnchor(cursor.AtLineStart ? packed : packed | ContinuationFlag, cursor.LineNumber));

        // Publish the rows of the PREVIOUS buckets (all boundaries below this anchor are
        // determined). Row count before waiter wake-up, so a released waiter sees it.
        Volatile.Write(ref this.publishedRowCount, rowsSoFar);
        this.OnItemsPublished(this.items.Count);
    }
}
