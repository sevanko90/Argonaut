using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Editing;

namespace Argonaut.Features.Raw.Rows;

/// <summary>
/// The row index of an edited document, layered over the (frozen, complete) index of the
/// original bytes.
///
/// <see cref="RawSegmentIndex.GetRowInfo"/> finds a row's anchor by dividing: anchor <i>i</i>
/// holds row 64<i>i</i>. An edit that changes how many rows a region occupies breaks that
/// arithmetic for every row after it, and re-scanning the tail of a multi-GB file per keystroke
/// is exactly what this whole design exists to avoid. So the original index is never rebuilt or
/// mutated - it stays a pure function of the original bytes, which is what makes it safe to keep
/// reading lock-free - and this class describes only what actually moved.
///
/// The document is therefore an alternation of two kinds of region:
///
///   dirty spans     runs of whole lines the edits touched, described here, one per place the
///                   user has edited
///   everything else rows straight from the original index, displaced by the constant deltas
///                   the spans before them introduced
///
/// <b>A span is a run of whole lines, and holds one record per line rather than any rows.</b>
/// Forced breaks are cap-anchored (<see cref="RawRowBoundary"/>): a line's rows are arithmetic
/// from where it starts and ends (<see cref="RawLineRows"/>), whatever bytes are in it. So a span
/// that begins at a line start and ends at a line end needs nothing but its line starts to answer
/// every row question, and the original lines either side of it are provably unchanged - their
/// bytes are the file's and their cap grids start where they always did. That is what makes an
/// edit cost the bytes it inserted plus the lines it touched, independent of how long those lines
/// are: an edit anywhere in a 4GB single-line document costs one 12-byte record.
///
/// It is also why the rule and this class changed together. Under the old rule, where a forced
/// break was measured from the previous row's end, an insert early in a long line could shift
/// every later break, and this class had to walk two byte streams to the line's newline to prove
/// they had rejoined - ~30ns a row, so ~40ms per keystroke early in a 100MB line. And line-start
/// spans that <i>stored</i> rows were tried before that and blew the budget on one long line.
/// Neither walking nor storing rows is needed now.
///
/// <b>Why several spans rather than one.</b> One span covering every edit would hold every line
/// between two edits a gigabyte apart. Spans are disjoint instead, so the cost tracks the number
/// of <i>places</i> edited rather than the distance between them. An edit joins every span its
/// lines touch; one on the line straight after a span joins it too, as long as the result stays
/// within <see cref="MaxLinesPerRun"/>. That cap is what bounds per-keystroke work inside a big
/// paste - an edit rebuilds the whole run it lands in - and a region needing more is emitted as
/// several adjacent runs.
///
/// Editing the same place repeatedly rebuilds one span; editing N places gives N spans. What is
/// <i>not</i> bounded is N itself, so <see cref="HeldLines"/> carries a budget - see
/// <see cref="NeedsRebuild"/> for when it runs out, what happens then, and why the rebuild it
/// names is a merge of the edits into a new baseline rather than anything to do with the bytes on
/// disk.
///
/// Not thread-safe, and deliberately requires a completed scan (see the constructor).
/// </summary>
public sealed class RawEditedRowIndex : IRawRowIndex
{
    /// <summary>
    /// How many line records may be held across every span before a full re-index is the better
    /// deal - a budget on <i>memory</i>, at 12 bytes each, so about 6MB. Unlike the anchor budget
    /// it replaced it is also, loosely, a budget on places: an ordinary edit costs one record.
    /// </summary>
    internal const int MaxHeldLines = 512 * 1024;

    /// <summary>
    /// Most lines one span may hold. Every edit rebuilds the whole span it lands in - its line
    /// records and their row counts, arithmetic but O(lines) - so this is what keeps typing inside
    /// a million-line paste at a few microseconds a keystroke rather than a few milliseconds.
    /// </summary>
    internal const int MaxLinesPerRun = 4096;

    private readonly RawSegmentIndex original;
    private readonly RawPieceTable document;
    private readonly int wrapWidth;
    private readonly int maxHeldLines;
    private readonly int maxLinesPerRun;

    /// <summary>Disjoint, ordered by position. Empty until the first edit.</summary>
    private readonly List<LineRun> spans = new();

    /// <summary>Line starts of the region an edit is rebuilding, in edited-document offsets.
    /// Kept between edits so a keystroke allocates nothing.</summary>
    private readonly List<long> rebuiltLineStarts = new();

    private int totalRowDelta;
    private int heldLines;
    private long bytesScannedInLastEdit;
    private int linesRebuiltInLastEdit;

    /// <param name="original">Index over the document's original bytes. Must be complete:
    /// editing is gated on a finished scan precisely so a lock-free append log is never read
    /// while a mutating coordinate system is layered on top of it.</param>
    /// <param name="document">The edited document, over the same original bytes.</param>
    /// <param name="maxHeldLines">Test seam. The production budget (<see cref="MaxHeldLines"/>)
    /// is half a million line records, which a test would need half a million edit sites to
    /// reach; a test that wants to see what happens at the budget passes a smaller one and
    /// exercises the same code.</param>
    /// <param name="maxLinesPerRun">Test seam for <see cref="MaxLinesPerRun"/>, for the same
    /// reason.</param>
    public RawEditedRowIndex(
        RawSegmentIndex original,
        RawPieceTable document,
        int maxHeldLines = MaxHeldLines,
        int maxLinesPerRun = MaxLinesPerRun)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLinesPerRun, 1);
        if (!original.AllItemsPublished)
            throw new ArgumentException("The scan must have finished before edits are layered on it.", nameof(original));

        this.original = original;
        this.document = document;
        this.wrapWidth = original.WrapWidth;
        this.maxHeldLines = maxHeldLines;
        this.maxLinesPerRun = maxLinesPerRun;
    }

    /// <summary>Rows in the edited document.</summary>
    public int RowCount => this.original.RowCount + this.totalRowDelta;

    /// <summary>
    /// <b>When it fires:</b> re-evaluated after every edit, and true once the spans together hold
    /// more than <see cref="MaxHeldLines"/> line records - around half a million separate places
    /// edited, or pastes adding that many lines. It can go false again: undoing enough shrinks the
    /// spans back.
    ///
    /// <b>What it means today:</b> nothing happens automatically. Row lookups stay correct either
    /// way - this is an efficiency signal, never a correctness one - and the only observable
    /// effect is <see cref="CanAbsorbEditAt"/> refusing to open a span in a place that is not
    /// already being edited.
    ///
    /// <b>What a rebuild would have to be, and why it is not here yet.</b> It is emphatically not
    /// "re-index the file": the rows on screen come from the piece table, and the bytes on disk
    /// are a document the user is no longer looking at. A rebuild has to scan the
    /// <i>piece table</i> - and a scan is only sound over bytes that then never change. So the
    /// shape is: freeze the current piece table, scan it into a complete
    /// <see cref="RawSegmentIndex"/>, and layer a fresh single-piece <see cref="RawPieceTable"/>
    /// over the frozen one, which becomes the new baseline. That is the collapse back to one
    /// piece <see cref="RawPieceTable"/>'s own remarks refer to, and it is a <i>merge</i> of the
    /// edits into the baseline rather than a re-read of anything.
    ///
    /// Three consequences are why that is sequenced with saving rather than shipped alongside
    /// typing. Edits must be frozen for the whole scan, which on a multi-GB document is seconds
    /// to minutes of a blocked editor. <see cref="RawEditJournal"/>'s undo snapshots belong to the
    /// outgoing piece table, so undo history either ends at a rebuild or has to learn to cross
    /// one. And each rebuild adds a layer, so every byte read afterwards pays one more binary
    /// search - bounded in practice, unbounded in principle. Saving solves the same problem more
    /// cheaply for the case that actually motivates it, because a save rewrites the file and
    /// starts again from a single piece over it.
    /// </summary>
    public bool NeedsRebuild { get; private set; }

    /// <summary>Rows the spans cover - what is computed from line records rather than read from
    /// the original index. Informational: the budget is <see cref="HeldLines"/>, since a row
    /// inside a span costs nothing to hold.</summary>
    internal int TotalDerivedRows
    {
        get
        {
            int total = 0;
            foreach (var span in this.spans)
                total += span.RowsHeld;

            return total;
        }
    }

    /// <summary>Line records held across every span. What the budget counts.</summary>
    internal int HeldLines => this.heldLines;

    /// <summary>Places the user has edited, as this index has grouped them.</summary>
    internal int SpanCount => this.spans.Count;

    /// <summary>
    /// Bytes the last edit read from the document: the inserted ones, searched for newlines, and
    /// nothing else. Shown by the debug inspector alongside <see cref="LinesRebuiltInLastEdit"/>,
    /// because between them they are the whole cost of a keystroke - and because "typing here is
    /// slow and typing there is not" is otherwise something a user can only describe rather than
    /// see.
    /// </summary>
    internal long BytesScannedInLastEdit => this.bytesScannedInLastEdit;

    /// <summary>Line records the last edit rebuilt: the lines of every span it joined, plus the
    /// ones it created.</summary>
    internal int LinesRebuiltInLastEdit => this.linesRebuiltInLastEdit;

    /// <summary>Rows in the original index, before any of this class's displacement.</summary>
    internal int OriginalRowCount => this.original.RowCount;

    /// <summary>
    /// The spans, flattened for display. Copies rather than exposing <see cref="LineRun"/>, which
    /// is mutable and whose fields only mean anything next to the spans either side of it.
    /// </summary>
    internal IReadOnlyList<RawSpanSnapshot> DescribeSpans()
    {
        var described = new List<RawSpanSnapshot>(this.spans.Count);
        for (int i = 0; i < this.spans.Count; i++)
        {
            var span = this.spans[i];
            described.Add(new RawSpanSnapshot(
                i,
                span.OriginalStartRow,
                span.OriginalRowsEnd,
                span.StartRow,
                span.StartOffset,
                span.EndOffset,
                span.RowsHeld,
                span.LineCount,
                span.FirstLineNumber,
                span.ByteDelta,
                span.RowDelta,
                span.LineDelta,
                span.ByteDeltaBefore,
                span.RowDeltaBefore,
                span.LineDeltaBefore));
        }

        return described;
    }

    /// <summary>
    /// Whether an edit at <paramref name="offset"/> can still be tracked, asked <i>before</i> the
    /// edit is made.
    ///
    /// An edit inside or at the end of an existing span is always allowed: rebuilding a span is
    /// bounded by that span alone. What is refused is opening a <i>new</i> span once the spans
    /// together have exhausted <see cref="MaxHeldLines"/> - at which point the honest answer is a
    /// background re-index over the piece table, and until that exists a refusal beats quietly
    /// allocating.
    /// </summary>
    public bool CanAbsorbEditAt(long offset)
    {
        if (!NeedsRebuild)
            return true;

        int at = SpanAtOrBefore(offset);
        return at >= 0 && offset <= this.spans[at].EndOffset;
    }

    /// <summary>
    /// Folds one edit in: the lines it touched, widened to the whole of every span among them,
    /// are replaced by one run (or, past <see cref="MaxLinesPerRun"/>, several) describing what
    /// those lines are now.
    ///
    /// The spans still describe the <i>pre-edit</i> document when this runs, and the original is
    /// immutable, so every question about where the touched lines began and ended is answered
    /// from them - in pre-edit coordinates, with the removed range at
    /// <c>[Offset, Offset + BytesRemoved)</c>. The only bytes read from the document are the
    /// inserted ones, which are valid post-edit at <c>[Offset, Offset + BytesInserted)</c>, and one
    /// byte to see whether the rebuilt region ends in a newline.
    /// </summary>
    public void ApplyEdit(RawEditExtent extent)
    {
        long editStart = extent.Offset;
        long removedEnd = extent.Offset + extent.BytesRemoved;
        long byteDelta = extent.ByteDelta;
        long preEditLength = this.document.AvailableLength - byteDelta;

        var head = PreEditLineStart(editStart, preEditLength);
        var tail = PreEditLineEnd(removedEnd, preEditLength);

        // Spans the touched lines overlap: the one holding the edit's first line, the one
        // holding its last, and any in between (which the edit deleted).
        int first = head.Span >= 0 ? head.Span : SpanAtOrBefore(editStart) + 1;
        int last = tail.Span >= 0 ? tail.Span : SpanAtOrBefore(removedEnd);

        // Line starts of the rebuilt region, in edited-document offsets: the lines before the
        // edit's (only when widened to a span), the edit's own, one per inserted newline, and the
        // lines after the edit's last (again only when widened).
        var lineStarts = this.rebuiltLineStarts;
        lineStarts.Clear();

        long beginOffset, endOffset;
        int beginRow, endRow, beginLine, lineAfterEnd;

        if (head.Span >= 0)
        {
            var span = this.spans[head.Span];
            for (int line = 0; line <= head.Line; line++)
                lineStarts.Add(span.StartOffset + span.LineStarts[line]);

            (beginOffset, beginRow, beginLine) = (span.OriginalStartOffset, span.OriginalStartRow, span.OriginalStartLine);
        }
        else
        {
            lineStarts.Add(head.Start);
            (beginOffset, beginRow, beginLine) = (head.OriginalStart, head.OriginalRow, head.OriginalLine);
        }

        ScanInsertedNewlines(editStart, extent.BytesInserted, lineStarts);

        long regionEnd;
        if (tail.Span >= 0)
        {
            var span = this.spans[tail.Span];
            for (int line = tail.Line + 1; line < span.LineCount; line++)
                lineStarts.Add(span.StartOffset + span.LineStarts[line] + byteDelta);

            regionEnd = span.EndOffset + byteDelta;
            (endOffset, endRow, lineAfterEnd) = (span.OriginalEndOffset, span.OriginalRowsEnd, span.OriginalStartLine + span.OriginalLinesReplaced);
        }
        else
        {
            regionEnd = tail.End + byteDelta;
            (endOffset, endRow, lineAfterEnd) = (tail.OriginalEnd, tail.OriginalRowsEnd, tail.OriginalLineAfter);
        }

        // A span on the line straight before or after joins in too, while the result stays within
        // the per-run cap - fewer, larger spans for the same records, without rebuilding a full
        // one on every keystroke next to it.
        if (first - 1 >= 0 && this.spans[first - 1].EndOffset == lineStarts[0]
                           && this.spans[first - 1].LineCount + lineStarts.Count <= this.maxLinesPerRun)
        {
            var span = this.spans[--first];
            int count = lineStarts.Count;
            CollectionsMarshal.SetCount(lineStarts, count + span.LineCount);
            var shifted = CollectionsMarshal.AsSpan(lineStarts);
            shifted[..count].CopyTo(shifted[span.LineCount..]);
            for (int line = 0; line < span.LineCount; line++)
                shifted[line] = span.StartOffset + span.LineStarts[line];

            (beginOffset, beginRow, beginLine) = (span.OriginalStartOffset, span.OriginalStartRow, span.OriginalStartLine);
        }

        if (last + 1 < this.spans.Count && this.spans[last + 1].StartOffset + byteDelta == regionEnd
                                        && this.spans[last + 1].LineCount + lineStarts.Count <= this.maxLinesPerRun)
        {
            var span = this.spans[++last];
            for (int line = 0; line < span.LineCount; line++)
                lineStarts.Add(span.StartOffset + span.LineStarts[line] + byteDelta);

            regionEnd = span.EndOffset + byteDelta;
            (endOffset, endRow, lineAfterEnd) = (span.OriginalEndOffset, span.OriginalRowsEnd, span.OriginalStartLine + span.OriginalLinesReplaced);
        }

        // A newline inserted as the region's very last byte ends its last line rather than
        // starting another: whatever follows is the next line, outside the region.
        long regionStart = lineStarts[0];
        if (lineStarts.Count > 1 && lineStarts[^1] == regionEnd)
            lineStarts.RemoveAt(lineStarts.Count - 1);

        bool terminated = regionEnd > regionStart
                          && RawRowBoundary.ByteAt(this.document, regionEnd - 1) == (byte)'\n';

        // Replace spans first..last with the rebuilt run(s). The first span replaced is reused
        // so that the ordinary keystroke - one edit inside one span - allocates nothing.
        LineRun? reusable = first <= last ? this.spans[first] : null;
        int removed = 0;
        for (int i = first; i <= last; i++)
            removed += this.spans[i].LineCount;

        if (last >= first)
            this.spans.RemoveRange(first, last - first + 1);

        int insertAt = first;
        for (int chunkStart = 0; chunkStart < lineStarts.Count; chunkStart += this.maxLinesPerRun)
        {
            int chunkEnd = Math.Min(chunkStart + this.maxLinesPerRun, lineStarts.Count);
            bool lastChunk = chunkEnd == lineStarts.Count;
            var run = chunkStart == 0 && reusable is not null ? reusable : new LineRun();

            // The first chunk replaces the whole original region; any after it replace nothing,
            // at the region's original end. That keeps every running total exact without the
            // chunk boundaries - which exist only in the edited document - meaning anything in
            // the original.
            if (chunkStart == 0)
            {
                run.OriginalStartOffset = beginOffset;
                run.OriginalStartRow = beginRow;
                run.OriginalStartLine = beginLine;
                run.OriginalExtent = endOffset - beginOffset;
                run.OriginalRowsEnd = endRow;
                run.OriginalLinesReplaced = lineAfterEnd - beginLine;
            }
            else
            {
                run.OriginalStartOffset = endOffset;
                run.OriginalStartRow = endRow;
                run.OriginalStartLine = lineAfterEnd;
                run.OriginalExtent = 0;
                run.OriginalRowsEnd = endRow;
                run.OriginalLinesReplaced = 0;
            }

            long runStart = lineStarts[chunkStart];
            long runEnd = lastChunk ? regionEnd : lineStarts[chunkEnd];
            run.Extent = runEnd - runStart;
            run.Terminated = lastChunk ? terminated : true;
            run.Describe(lineStarts, chunkStart, chunkEnd, this.wrapWidth);

            this.spans.Insert(insertAt++, run);
        }

        this.heldLines += lineStarts.Count - removed;
        this.bytesScannedInLastEdit = extent.BytesInserted;
        this.linesRebuiltInLastEdit = lineStarts.Count;

        Renumber(first);

        // Spans are never dropped, even once everything in one has been undone: nothing here
        // proves the original index describes those lines again, and a span describing unchanged
        // lines is merely redundant, never wrong.
        NeedsRebuild = this.heldLines > this.maxHeldLines;
    }

    /// <summary>
    /// Adds a line start after every '\n' in the inserted bytes - the only bytes an edit reads,
    /// looping on <see cref="IByteSource.GetContiguousSpan"/> because an insert may straddle
    /// pieces.
    /// </summary>
    private void ScanInsertedNewlines(long offset, long length, List<long> lineStarts)
    {
        long end = offset + length;
        while (offset < end && RawRowBoundary.IndexOfNewline(this.document, offset, end - offset) is long newline)
        {
            lineStarts.Add(newline + 1);
            offset = newline + 1;
        }
    }

    public RawRowInfo GetRowInfo(int rowIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rowIndex, RowCount);

        int at = SpanAtOrBeforeRow(rowIndex);
        if (at < 0)
            return this.original.GetRowInfo(rowIndex);

        var span = this.spans[at];
        int withinSpan = rowIndex - span.StartRow;
        if (withinSpan < span.RowsHeld)
        {
            int line = span.LineAtRow(withinSpan);
            int row = withinSpan - span.RowPrefix[line];
            var (start, end, softWrap) = span.Rows(line, this.wrapWidth).Range(this.document, row);
            return new RawRowInfo(start, end, softWrap, row == 0 ? span.FirstLineNumber + line : null);
        }

        return span.Displace(this.original.GetRowInfo(rowIndex - span.RowDeltaThrough));
    }

    /// <summary>
    /// The row containing <paramref name="offset"/> in the edited document, or null when the
    /// offset is past its end.
    /// </summary>
    public int? RowForOffset(long offset)
    {
        if (offset < 0 || offset >= this.document.AvailableLength)
            return null;

        int at = SpanAtOrBefore(offset);
        if (at < 0)
            return this.original.RowForOffset(offset);

        var span = this.spans[at];
        if (offset < span.EndOffset)
        {
            int line = span.LineAt(offset - span.StartOffset);
            return span.StartRow + span.RowPrefix[line] + span.Rows(line, this.wrapWidth).RowContaining(this.document, offset);
        }

        int? originalRow = this.original.RowForOffset(offset - span.ByteDeltaThrough);
        return originalRow is null ? null : originalRow.Value + span.RowDeltaThrough;
    }

    /// <summary>
    /// See <see cref="IRawRowIndex.LineContaining"/>. The same three cases
    /// <see cref="GetRowInfo"/> has: rows no edit reached come straight from the original index;
    /// rows inside a span are numbered by the line record they fall in; and rows past a span are
    /// the original's answer shifted by the line delta the spans before them introduced.
    /// </summary>
    public int? LineContaining(int rowIndex)
    {
        if ((uint)rowIndex >= (uint)RowCount)
            return null;

        int at = SpanAtOrBeforeRow(rowIndex);
        if (at < 0)
            return this.original.LineContaining(rowIndex);

        var span = this.spans[at];
        int withinSpan = rowIndex - span.StartRow;
        if (withinSpan < span.RowsHeld)
            return span.FirstLineNumber + span.LineAtRow(withinSpan);

        return this.original.LineContaining(rowIndex - span.RowDeltaThrough) is int originalLine
            ? originalLine + span.LineDeltaThrough
            : null;
    }

    // ---- pre-edit line lookups -------------------------------------------------------------

    /// <summary>
    /// The line holding pre-edit offset <paramref name="offset"/>, as where it begins. Either a
    /// line of a span (<see cref="LineHead.Span"/> ≥ 0) - which the rebuilt region will widen to
    /// the whole of - or a line the original describes entirely, with its original coordinates.
    ///
    /// Spans consist of whole lines, so an offset outside every span is in a line that is outside
    /// every span too. The one line no span or original line holds is the phantom empty one after
    /// a trailing newline: after a span ending the document it is described from that span's end,
    /// since the original's own last byte says nothing about the edited one.
    /// </summary>
    private LineHead PreEditLineStart(long offset, long preEditLength)
    {
        int at = SpanAtOrBefore(offset);
        if (at >= 0)
        {
            var span = this.spans[at];
            if (HoldsLineAt(span, offset, preEditLength))
            {
                int line = offset < span.EndOffset ? span.LineAt(offset - span.StartOffset) : span.LineCount - 1;
                return new LineHead(at, line, span.StartOffset + span.LineStarts[line], 0, 0, 0);
            }

            if (offset == span.EndOffset && offset == preEditLength)
                return new LineHead(-1, 0, offset, span.OriginalEndOffset, span.OriginalRowsEnd, span.OriginalStartLine + span.OriginalLinesReplaced);
        }

        long byteDelta = at >= 0 ? this.spans[at].ByteDeltaThrough : 0;
        var (start, firstRow, lineNumber) = this.original.LineStartContaining(offset - byteDelta);
        return new LineHead(-1, 0, start + byteDelta, start, firstRow, lineNumber);
    }

    /// <summary>The counterpart of <see cref="PreEditLineStart"/>: where the line holding
    /// <paramref name="offset"/> ends, and the original coordinates of that end.</summary>
    private LineTail PreEditLineEnd(long offset, long preEditLength)
    {
        int at = SpanAtOrBefore(offset);
        if (at >= 0)
        {
            var span = this.spans[at];
            if (HoldsLineAt(span, offset, preEditLength))
            {
                int line = offset < span.EndOffset ? span.LineAt(offset - span.StartOffset) : span.LineCount - 1;
                return new LineTail(at, line, span.EndOffset, 0, 0, 0);
            }

            if (offset == span.EndOffset && offset == preEditLength)
                return new LineTail(-1, 0, offset, span.OriginalEndOffset, span.OriginalRowsEnd, span.OriginalStartLine + span.OriginalLinesReplaced + 1);
        }

        long byteDelta = at >= 0 ? this.spans[at].ByteDeltaThrough : 0;
        var (end, _, rowsEnd, lineNumber) = this.original.LineEndContaining(offset - byteDelta);
        return new LineTail(-1, 0, end + byteDelta, end, rowsEnd, lineNumber + 1);
    }

    /// <summary>
    /// Whether <paramref name="offset"/> sits in one of <paramref name="span"/>'s lines. Its end
    /// is the next line's start - outside it - except at the end of the document, where it is
    /// still in the span's last line unless that line ended in a newline.
    /// </summary>
    private static bool HoldsLineAt(LineRun span, long offset, long preEditLength)
        => offset < span.EndOffset
           || (offset == span.EndOffset && offset == preEditLength && !span.Terminated);

    private readonly record struct LineHead(int Span, int Line, long Start, long OriginalStart, int OriginalRow, int OriginalLine);

    private readonly record struct LineTail(int Span, int Line, long End, long OriginalEnd, int OriginalRowsEnd, int OriginalLineAfter);

    // ---- span bookkeeping -----------------------------------------------------------------

    /// <summary>Index of the last span starting at or before <paramref name="offset"/>, or -1.</summary>
    private int SpanAtOrBefore(long offset)
    {
        int lo = 0, hi = this.spans.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (this.spans[mid].StartOffset <= offset)
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

    /// <summary>Index of the last span starting at or before <paramref name="rowIndex"/>, or -1.</summary>
    private int SpanAtOrBeforeRow(int rowIndex)
    {
        int lo = 0, hi = this.spans.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (this.spans[mid].StartRow <= rowIndex)
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

    /// <summary>
    /// Recomputes each span's running total of what the spans before it did, from
    /// <paramref name="index"/> onwards, and the document-wide row delta.
    ///
    /// O(spans after the edit) rather than O(1), which is the same trade
    /// <see cref="RawPieceTable.RenumberFrom"/> makes and for the same reason: keeping the
    /// prefixes materialised is what lets every <i>read</i> be a plain binary search, and reads
    /// vastly outnumber edits.
    /// </summary>
    private void Renumber(int index)
    {
        for (int i = Math.Max(index, 0); i < this.spans.Count; i++)
        {
            var span = this.spans[i];
            if (i == 0)
            {
                span.ByteDeltaBefore = 0;
                span.RowDeltaBefore = 0;
                span.LineDeltaBefore = 0;
            }
            else
            {
                var previous = this.spans[i - 1];
                span.ByteDeltaBefore = previous.ByteDeltaThrough;
                span.RowDeltaBefore = previous.RowDeltaThrough;
                span.LineDeltaBefore = previous.LineDeltaThrough;
            }
        }

        this.totalRowDelta = this.spans.Count == 0 ? 0 : this.spans[^1].RowDeltaThrough;
    }

    /// <summary>
    /// One place the document has been edited: a run of whole lines, what they replaced in the
    /// original, and the running totals of every span before it.
    ///
    /// Everything it holds is either relative to its own start or a property of the original
    /// index, so a span is untouched by edits anywhere else - which is the whole point of there
    /// being more than one.
    /// </summary>
    private sealed class LineRun
    {
        /// <summary>Where the replaced original region begins: offset, first row, line number.</summary>
        public long OriginalStartOffset;
        public int OriginalStartRow;
        public int OriginalStartLine;

        /// <summary>What was replaced: bytes, the row after its last, and lines (the phantom empty
        /// line after a trailing newline counts, so the line arithmetic needs no special case).</summary>
        public long OriginalExtent;
        public int OriginalRowsEnd;
        public int OriginalLinesReplaced;

        /// <summary>Line starts relative to <see cref="StartOffset"/>; the first is always 0.</summary>
        public readonly List<long> LineStarts = new();

        /// <summary>Rows before each line; <see cref="RowsHeld"/> is the total.</summary>
        public readonly List<int> RowPrefix = new();

        public int RowsHeld;

        /// <summary>Bytes this span covers, relative to <see cref="StartOffset"/>.</summary>
        public long Extent;

        /// <summary>Whether the last line ends in '\n'. Only a span ending the document can say
        /// no; every other one ends where the next line starts.</summary>
        public bool Terminated;

        public long ByteDeltaBefore;
        public int RowDeltaBefore;
        public int LineDeltaBefore;

        public int LineCount => LineStarts.Count;

        public long OriginalEndOffset => OriginalStartOffset + OriginalExtent;

        public long ByteDelta => Extent - OriginalExtent;
        public int RowDelta => RowsHeld - (OriginalRowsEnd - OriginalStartRow);
        public int LineDelta => LineStarts.Count - OriginalLinesReplaced;

        public long ByteDeltaThrough => ByteDeltaBefore + ByteDelta;
        public int RowDeltaThrough => RowDeltaBefore + RowDelta;
        public int LineDeltaThrough => LineDeltaBefore + LineDelta;

        /// <summary>First row of this span, in edited-document row space.</summary>
        public int StartRow => OriginalStartRow + RowDeltaBefore;

        /// <summary>Number of this span's first line, in the edited document.</summary>
        public int FirstLineNumber => OriginalStartLine + LineDeltaBefore;

        /// <summary>First byte of this span, in edited-document byte space.</summary>
        public long StartOffset => OriginalStartOffset + ByteDeltaBefore;

        /// <summary>Exclusive end of this span, in edited-document byte space.</summary>
        public long EndOffset => StartOffset + Extent;

        /// <summary>
        /// Fills the line records from <paramref name="lineStarts"/>[from..to), absolute offsets
        /// whose first is this span's start. Row counts are arithmetic - no bytes read.
        /// </summary>
        public void Describe(List<long> lineStarts, int from, int to, int wrapWidth)
        {
            LineStarts.Clear();
            RowPrefix.Clear();
            long start = lineStarts[from];
            for (int i = from; i < to; i++)
                LineStarts.Add(lineStarts[i] - start);

            // Row counts depend only on where each line starts and ends, never on where the span
            // itself sits, so they are right even before the running totals are renumbered.
            int rows = 0;
            for (int line = 0; line < LineStarts.Count; line++)
            {
                RowPrefix.Add(rows);
                rows += Rows(line, wrapWidth).Count;
            }

            RowsHeld = rows;
        }

        /// <summary>The rows of line <paramref name="line"/>, in document offsets.</summary>
        public RawLineRows Rows(int line, int wrapWidth)
        {
            long lineStart = StartOffset + LineStarts[line];
            if (line + 1 < LineStarts.Count)
                return new RawLineRows(lineStart, StartOffset + LineStarts[line + 1] - 1, true, wrapWidth);

            return new RawLineRows(lineStart, Terminated ? EndOffset - 1 : EndOffset, Terminated, wrapWidth);
        }

        /// <summary>The line holding <paramref name="relative"/>: the last starting at or before it.</summary>
        public int LineAt(long relative)
        {
            int lo = 0, hi = LineStarts.Count - 1, found = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (LineStarts[mid] <= relative)
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

        /// <summary>The line holding row <paramref name="withinSpan"/> of this span.</summary>
        public int LineAtRow(int withinSpan)
        {
            int lo = 0, hi = RowPrefix.Count - 1, found = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (RowPrefix[mid] <= withinSpan)
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

        /// <summary>An original row that falls after this span, moved to where it now sits.</summary>
        public RawRowInfo Displace(RawRowInfo info)
            => new(info.Start + ByteDeltaThrough,
                   info.End + ByteDeltaThrough,
                   info.IsSoftWrapped,
                   info.LineNumber is int line ? line + LineDeltaThrough : null);
    }
}
