using System;
using System.Collections.Generic;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// The row index of an edited document, layered over the (frozen, complete) index of the
/// original bytes.
///
/// <see cref="RawSegmentIndex.GetRowInfo"/> finds a row's anchor by dividing: anchor <i>i</i>
/// holds row 64<i>i</i>. An edit that changes how many rows a region occupies breaks that
/// arithmetic for every row after it, and re-scanning the tail of a multi-GB file per keystroke
/// is exactly what this whole design exists to avoid. So the original index is never rebuilt or
/// mutated - it stays a pure function of the original bytes, which is what makes it safe to keep
/// reading lock-free - and this class re-derives only what actually moved.
///
/// The document is therefore an alternation of two kinds of region:
///
///   dirty spans     re-derived over the edited bytes and held in full, one per place the user
///                   has edited
///   everything else rows straight from the original index, displaced by the constant deltas
///                   the spans before them introduced
///
/// <b>Why several spans rather than one.</b> The first cut coalesced every edit into a single
/// span running from the earliest edit to wherever re-derivation rejoined the original. That is
/// correct but it holds every row in between, so changing two characters a gigabyte apart meant
/// materialising every row of the gigabyte between them - and the edit had to be refused to stop
/// it. Spans are disjoint instead, each one bounded by the anchor it starts at and the point its
/// two byte streams re-converge, so the cost tracks the number of <i>places</i> edited rather
/// than the distance between them.
///
/// <b>What decides between widening a span and starting a new one</b> is the index's own anchor
/// stride, not a tuned threshold. A span can only begin at an anchor, because an anchor is the
/// only place the original stream's state (offset, line number, whether a line starts there) is
/// known without walking to it - so a new span already costs up to <see cref="RawSegmentIndex.AnchorStride"/>
/// rows of walking before it even reaches the edit. An edit closer than that to an existing span
/// is therefore cheaper to absorb into it, and one further away is cheaper to keep separate.
/// Part of that rule is not about cost at all: an edit in an anchor bucket the previous span has
/// already re-derived past <i>must</i> join it, because two overlapping spans make every binary
/// search here meaningless. A re-derivation that runs past the span <i>after</i> it swallows that
/// one instead (see <see cref="Rederive"/>); between them, spans stay disjoint and ordered.
///
/// Editing the same place repeatedly widens one span; editing N places gives N spans; going back
/// and forth between two places gives two. What is <i>not</i> bounded is N itself, so
/// <see cref="HeldAnchors"/> carries a budget - see <see cref="NeedsRebuild"/> for when it runs
/// out, what happens then, and why the rebuild it names is a merge of the edits into a new
/// baseline rather than anything to do with the bytes on disk.
///
/// <b>A span stores anchors, not rows</b>, for exactly the reason <see cref="RawSegmentIndex"/>
/// does: one marker every <see cref="RawSegmentIndex.AnchorStride"/> rows, and any row in between
/// recovered by re-walking from it. The first cut held every row it derived, which is fine while
/// a span is the sixty-odd rows an ordinary edit disturbs and is not fine at all when it is not -
/// a 48-byte edit inside a 54MB unbroken line produced a span of 676,661 rows and 21MB of
/// <see cref="RawRowInfo"/>, which blew the budget tenfold and locked editing out everywhere
/// else. The same edit now costs about 10,500 anchors and 170KB.
///
/// That a long line has to be walked at all is not avoidable, and the appealing shortcut is
/// unsound. Inside a soft-wrapped line the breaks fall every <c>WrapWidth</c> bytes from the line
/// start, so it is tempting to say an insert leaves every later break where it was and converge
/// immediately at zero displacement. <see cref="RawRowBoundary"/> backs a forced break off by up
/// to 3 bytes to avoid splitting a UTF-8 character, and which bytes sit at the cap has just
/// changed - so one different backoff moves the next row, and that chains. It holds for ASCII and
/// cannot be assumed, which is not a standard a row index gets to work to.
///
/// Re-derivation walks two streams from the same anchor - one over the original bytes, one over
/// the edited document - and stops when they provably re-converge: past every edit in that span,
/// offsets differing by exactly the delta accumulated so far, and agreeing on whether the row
/// starts a line. Past the last edit the bytes are identical, and a row's extent depends only on
/// the bytes from its start, so from that point the streams cannot diverge again. Nothing here
/// guesses that re-flow "usually" settles - a forced break backs off up to 3 bytes to avoid
/// splitting a UTF-8 character, so a single inserted byte can shift every later break in a long
/// line.
///
/// Not thread-safe, and deliberately requires a completed scan (see the constructor).
/// </summary>
public sealed class RawEditedRowIndex : IRawRowIndex
{
    /// <summary>
    /// How many anchors may be held across every dirty span before a full re-index is the better
    /// deal - a budget on <i>memory</i>, at 16 bytes each, so about 1MB.
    ///
    /// It is deliberately not a budget on time, which the anchors do not bound: re-deriving a
    /// span walks every row it covers, measured at roughly 30ns per row (Apple M5, Release), so
    /// an edit inside a 32MB unbroken line costs about 13ms per keystroke and one inside a 54MB
    /// line about 20ms - laggy but usable, where before this class held those rows in full and
    /// cost 21MB and a blown budget instead. Bounding the walk is what the background re-index
    /// over the piece table is for; bounding what is kept is this.
    /// </summary>
    internal const int MaxHeldAnchors = 64 * 1024;

    private readonly RawSegmentIndex original;
    private readonly IByteSource originalBytes;
    private readonly RawPieceTable document;
    private readonly int wrapWidth;
    private readonly long originalLength;
    private readonly int maxHeldAnchors;

    /// <summary>Disjoint, ordered by position. Empty until the first edit.</summary>
    private readonly List<DirtySpan> spans = new();

    private int totalRowDelta;

    /// <param name="original">Index over <paramref name="originalBytes"/>. Must be complete:
    /// editing is gated on a finished scan precisely so a lock-free append log is never read
    /// while a mutating coordinate system is layered on top of it.</param>
    /// <param name="originalBytes">The bytes <paramref name="original"/> indexed.</param>
    /// <param name="document">The edited document, over the same original bytes.</param>
    /// <param name="maxHeldAnchors">Test seam. The production budget
    /// (<see cref="MaxHeldAnchors"/>) is a megabyte of anchors, which a test would need a
    /// third of a gigabyte of document to reach; a test that wants to see what happens at the
    /// budget passes a smaller one and exercises the same code.</param>
    public RawEditedRowIndex(
        RawSegmentIndex original,
        IByteSource originalBytes,
        RawPieceTable document,
        int maxHeldAnchors = MaxHeldAnchors)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(document);
        if (!original.AllItemsPublished)
            throw new ArgumentException("The scan must have finished before edits are layered on it.", nameof(original));

        this.original = original;
        this.originalBytes = originalBytes;
        this.document = document;
        this.wrapWidth = original.WrapWidth;
        this.originalLength = originalBytes.AvailableLength;
        this.maxHeldAnchors = maxHeldAnchors;
    }

    /// <summary>Rows in the edited document.</summary>
    public int RowCount => this.original.RowCount + this.totalRowDelta;

    /// <summary>
    /// <b>When it fires:</b> re-evaluated after every edit, and true once the spans together hold
    /// more than <see cref="MaxHeldAnchors"/> anchors. There are two ways to get there - around a
    /// thousand separate places edited, each costing its own anchor bucket, or a handful of edits
    /// inside lines long enough that walking the rest of them runs to that many anchors on their
    /// own. It can go false again: undoing enough shrinks the spans back.
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

    /// <summary>Rows the spans cover - what is re-derived rather than read from the original
    /// index. Informational: the budget is <see cref="HeldAnchors"/>, since a row inside a span
    /// costs nothing to hold, only to re-walk.</summary>
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

    /// <summary>Anchors held across every span. What the budget counts.</summary>
    internal int HeldAnchors
    {
        get
        {
            int total = 0;
            foreach (var span in this.spans)
                total += span.Anchors.Count;

            return total;
        }
    }

    /// <summary>Places the user has edited, as this index has grouped them.</summary>
    internal int SpanCount => this.spans.Count;

    /// <summary>Rows in the original index, before any of this class's displacement.</summary>
    internal int OriginalRowCount => this.original.RowCount;

    /// <summary>
    /// The spans, flattened for display. Copies rather than exposing <see cref="DirtySpan"/>,
    /// which is mutable and whose fields only mean anything next to the spans either side of it.
    /// </summary>
    internal IReadOnlyList<RawSpanSnapshot> DescribeSpans()
    {
        var described = new List<RawSpanSnapshot>(this.spans.Count);
        for (int i = 0; i < this.spans.Count; i++)
        {
            var span = this.spans[i];

            int? firstLine = span.Anchors.Count > 0 ? span.Anchors[0].LineNumber + span.LineDeltaBefore : null;

            described.Add(new RawSpanSnapshot(
                i,
                span.OriginalAnchor,
                span.OriginalStartRow,
                span.StartRow,
                span.StartOffset,
                span.EndOffset,
                span.RowsHeld,
                span.Anchors.Count,
                span.ConvergedOriginalRow,
                span.EditReach,
                firstLine,
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
    /// An edit inside or near an existing span is always allowed: widening a span is bounded by
    /// that span alone. What is refused is opening a <i>new</i> span once the spans together have
    /// exhausted <see cref="MaxHeldAnchors"/> - at which point the honest answer is a background
    /// re-index over the piece table (roadmap: "More than one dirty span in RawEditedRowIndex"),
    /// and until that exists a refusal beats quietly allocating.
    /// </summary>
    public bool CanAbsorbEditAt(long offset)
    {
        if (!NeedsRebuild)
            return true;

        int at = SpanAtOrBefore(offset);
        return at >= 0 && offset <= this.spans[at].EndOffset;
    }

    /// <summary>
    /// Folds one edit in and re-derives whatever it disturbed. Cost is bounded by the one span
    /// the edit lands in, not by the file and not by the other spans.
    /// </summary>
    public void ApplyEdit(RawEditExtent extent)
    {
        int index = TargetSpanFor(extent.Offset);
        var span = this.spans[index];

        // The edit's own bytes. Tracked per span, relative to the span's start, so that spans
        // before it moving does not disturb it.
        long within = extent.Offset - span.StartOffset;
        if (within <= span.EditReach)
            span.EditReach += extent.ByteDelta;

        span.EditReach = Math.Max(span.EditReach, within + extent.BytesInserted);
        span.ByteDelta += extent.ByteDelta;

        // A deletion can swallow whole spans. Folding them in now rather than letting the absorb
        // loop discover them matters: until their deltas are part of this span's, the two byte
        // streams cannot re-converge, and the first derivation would walk to the end of the
        // document before finding that out.
        long removedEnd = extent.Offset + extent.BytesRemoved;
        while (index + 1 < this.spans.Count && this.spans[index + 1].StartOffset < removedEnd)
            Swallow(span, index + 1);

        // An edit cannot reach past the document, and a reach that did would be a convergence
        // point the derivation could never arrive at.
        span.EditReach = Math.Clamp(span.EditReach, 0, this.document.AvailableLength - span.StartOffset);

        Rederive(index);
    }

    /// <summary>Folds the span at <paramref name="index"/> into <paramref name="span"/> and drops
    /// it, preserving both the total byte delta and how far the edits reach.</summary>
    private void Swallow(DirtySpan span, int index)
    {
        var absorbed = this.spans[index];
        span.EditReach = Math.Max(span.EditReach, absorbed.StartOffset + absorbed.EditReach - span.StartOffset);
        span.ByteDelta += absorbed.ByteDelta;
        this.spans.RemoveAt(index);
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
            return WalkTo(span, withinSpan);

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
            // Inside the span: binary search its anchors, then walk the bucket - the same two
            // steps RawSegmentIndex.RowForOffset takes over the file's own anchors.
            long relative = offset - span.StartOffset;
            int bucket = span.BucketContaining(relative);

            var (start, atLineStart, line) = span.AnchorState(bucket);
            for (int row = bucket * RawSegmentIndex.AnchorStride; row < span.RowsHeld; row++)
            {
                var (end, softWrap) = RawRowBoundary.Next(this.document, this.wrapWidth, start);
                if (offset < end)
                    return span.StartRow + row;

                start = end;
            }

            return span.StartRow + span.RowsHeld - 1;
        }

        int? originalRow = this.original.RowForOffset(offset - span.ByteDeltaThrough);
        return originalRow is null ? null : originalRow.Value + span.RowDeltaThrough;
    }

    /// <summary>
    /// See <see cref="IRawRowIndex.LineContaining"/>. The same three cases
    /// <see cref="GetRowInfo"/> has: rows no edit reached come straight from the original index;
    /// rows inside a span carry their own line numbers, so the answer is the nearest line start
    /// at or before the row, falling back to the row before the span; and rows past a span are
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
        {
            // The walk GetRowInfo does, reporting the line it tracks on the way rather than
            // dropping it on a continuation row - RawSegmentIndex.LineContaining's split, for
            // the same reason: a gutter wants "nothing to draw", a caret readout wants "line 54".
            var (start, atLineStart, line) = span.AnchorState(withinSpan / RawSegmentIndex.AnchorStride);
            for (int row = (withinSpan / RawSegmentIndex.AnchorStride) * RawSegmentIndex.AnchorStride; row < withinSpan; row++)
            {
                var (end, softWrap) = RawRowBoundary.Next(this.document, this.wrapWidth, start);
                if (!softWrap)
                    line++;

                start = end;
            }

            return line + span.LineDeltaBefore;
        }

        return this.original.LineContaining(rowIndex - span.RowDeltaThrough) is int originalLine
            ? originalLine + span.LineDeltaThrough
            : null;
    }

    // ---- span selection -------------------------------------------------------------------

    /// <summary>
    /// The span an edit at <paramref name="offset"/> belongs to, creating one if the nearest is
    /// further off than a new span would cost to open.
    ///
    /// Offsets are read in pre-edit coordinates here even though the document has already been
    /// changed, which is legitimate because the <i>position</i> of a change means the same thing
    /// on both sides of it: everything below it is untouched, and everything above it belongs to
    /// spans this method is only comparing against, not indexing into.
    /// </summary>
    private int TargetSpanFor(long offset)
    {
        int at = SpanAtOrBefore(offset);
        if (at >= 0 && offset <= this.spans[at].EndOffset)
            return at; // inside a span, or exactly at its end

        long originalOffset = at < 0 ? offset : offset - this.spans[at].ByteDeltaThrough;
        int anchor = AnchorContaining(Math.Clamp(originalOffset, 0, Math.Max(this.originalLength - 1, 0)));

        if (at >= 0)
        {
            int gap = (anchor * RawSegmentIndex.AnchorStride) - this.spans[at].ConvergedOriginalRow;

            // Two separate reasons to widen the span before instead of opening a new one, and
            // only one of them is about cost.
            //
            // A new span can only begin at an anchor, and the anchor covering this edit may be
            // one the span before has already re-derived past - in which case the two would
            // overlap, and spans that overlap make every binary search here meaningless. Stated
            // separately because it is a correctness condition rather than a preference: the cost
            // rule below happens to subsume it today (a non-positive gap is also a small one), so
            // this is what stops a future change to the heuristic from quietly reintroducing
            // overlapping spans. Removing both is not a cost regression, it is wrong answers.
            bool wouldOverlap = gap <= 0;

            // And a new span pays for its own anchor bucket before it even reaches the edit, so
            // an edit closer than a stride is cheaper to absorb than to describe separately.
            bool cheaperToWiden = gap < RawSegmentIndex.AnchorStride;

            if (wouldOverlap || cheaperToWiden)
                return at;
        }

        var created = new DirtySpan
        {
            OriginalAnchor = anchor,
            OriginalStartOffset = this.original.RowCount == 0 ? 0 : this.original.AnchorAt(anchor).Start,
            ConvergedOriginalRow = anchor * RawSegmentIndex.AnchorStride,
        };

        this.spans.Insert(at + 1, created);
        Renumber(at + 1);
        return at + 1;
    }

    private int AnchorContaining(long originalOffset)
    {
        int? row = this.original.RowForOffset(originalOffset);
        int rowIndex = row ?? Math.Max(this.original.RowCount - 1, 0);
        return rowIndex / RawSegmentIndex.AnchorStride;
    }

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

    // ---- re-derivation --------------------------------------------------------------------

    /// <summary>
    /// Re-derives one span, then swallows any following span its re-derivation ran past.
    ///
    /// The absorb loop is what keeps spans disjoint without anyone having to predict where a
    /// re-derivation will stop: derive, see whether the next span's anchor now falls inside what
    /// was derived, and if so fold its edits in and derive again. It terminates because every
    /// turn removes a span.
    /// </summary>
    private void Rederive(int index)
    {
        var span = this.spans[index];

        while (true)
        {
            Derive(span);
            Renumber(index + 1);

            if (index + 1 >= this.spans.Count)
                break;

            if (this.spans[index + 1].OriginalStartRow > span.ConvergedOriginalRow)
                break;

            Swallow(span, index + 1);
        }

        // Spans are never dropped, even once everything in one has been undone. A span whose
        // deltas are all zero can still hold different rows from the original - a same-length
        // replacement changes which bytes a forced break backs off from - so "no delta" is not
        // evidence that the original index describes those rows again.
        NeedsRebuild = HeldAnchors > this.maxHeldAnchors;
    }

    /// <summary>
    /// Walks the original bytes and the edited document forward from one span's anchor in
    /// lockstep, holding the edited document's rows, until the two provably re-converge.
    ///
    /// The rows are stored <b>relative to the span's own start</b>, so a span is unaffected by
    /// anything that changes before it: an edit in an earlier span shifts this one's position
    /// without touching a single row it holds. Absolute answers are put back together in
    /// <see cref="DirtySpan.Absolute"/>.
    /// </summary>
    private void Derive(DirtySpan span)
    {
        span.Anchors.Clear();
        span.RowsHeld = 0;

        // An empty original has no anchors at all, but can still be typed into.
        var anchor = this.original.RowCount == 0
            ? (Start: 0L, AtLineStart: true, LineNumber: 1)
            : this.original.AnchorAt(span.OriginalAnchor);

        span.OriginalStartOffset = anchor.Start;

        long spanStart = span.StartOffset;
        long totalDelta = span.ByteDeltaBefore + span.ByteDelta;
        long documentLength = this.document.AvailableLength;
        long reachEnd = spanStart + span.EditReach;

        // The edited stream, which is what we keep. Line numbers are tracked in the original's
        // numbering and shifted by the spans before this one only on the way out.
        long currentStart = spanStart;
        bool currentAtLineStart = anchor.AtLineStart;
        int currentLine = anchor.LineNumber;

        // The original stream, walked only to recognise where the two rejoin.
        long originalStart = anchor.Start;
        bool originalAtLineStart = anchor.AtLineStart;
        int originalLine = anchor.LineNumber;
        int originalRow = span.OriginalStartRow;

        while (currentStart < documentLength)
        {
            var (end, softWrap) = RawRowBoundary.Next(this.document, this.wrapWidth, currentStart);

            // One marker per bucket, and the rows in between are re-walked on demand. Holding
            // every row instead is what made a long line cost 21MB (see the class remarks).
            if (span.RowsHeld % RawSegmentIndex.AnchorStride == 0)
                span.Anchors.Add(new SpanAnchor(currentStart - spanStart, currentLine, currentAtLineStart));

            span.RowsHeld++;
            span.Extent = end - spanStart;

            if (softWrap)
            {
                currentAtLineStart = false;
            }
            else
            {
                currentLine++;
                currentAtLineStart = true;
            }

            currentStart = end;

            // Bring the original stream up to the edited one, in the edited one's coordinates.
            while (originalStart < this.originalLength && originalStart + totalDelta < currentStart)
            {
                var (originalEnd, originalSoftWrap) = RawRowBoundary.Next(this.originalBytes, this.wrapWidth, originalStart);
                if (originalSoftWrap)
                {
                    originalAtLineStart = false;
                }
                else
                {
                    originalLine++;
                    originalAtLineStart = true;
                }

                originalStart = originalEnd;
                originalRow++;
            }

            bool converged =
                currentStart >= reachEnd &&
                originalStart < this.originalLength &&
                originalStart + totalDelta == currentStart &&
                originalAtLineStart == currentAtLineStart;

            if (converged)
            {
                span.ConvergedOriginalRow = originalRow;
                span.RowDelta = span.OriginalStartRow + span.RowsHeld - originalRow;
                span.LineDelta = currentLine - originalLine;
                return;
            }
        }

        // Walked to the end of the document without rejoining: this span runs to the end, and
        // there is no displaced tail after it to describe.
        span.ConvergedOriginalRow = this.original.RowCount;
        span.RowDelta = span.OriginalStartRow + span.RowsHeld - this.original.RowCount;
        span.LineDelta = 0;
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
    /// One marker inside a dirty span: where a bucket of <see cref="RawSegmentIndex.AnchorStride"/>
    /// rows begins, and the line state there. Sixteen bytes, against the 32 a
    /// <see cref="RawRowInfo"/> costs for a single row.
    /// </summary>
    private readonly record struct SpanAnchor(long Offset, int LineNumber, bool AtLineStart);

    /// <summary>
    /// One of a span's own rows, recovered by walking forward from its bucket's anchor - at most
    /// <see cref="RawSegmentIndex.AnchorStride"/> boundary computations, which is the same bounded
    /// rescan a plain lookup in the original index already does.
    /// </summary>
    private RawRowInfo WalkTo(DirtySpan span, int withinSpan)
    {
        int bucket = withinSpan / RawSegmentIndex.AnchorStride;
        var (start, atLineStart, line) = span.AnchorState(bucket);

        for (int row = bucket * RawSegmentIndex.AnchorStride; ; row++)
        {
            var (end, softWrap) = RawRowBoundary.Next(this.document, this.wrapWidth, start);
            if (row == withinSpan)
                return new RawRowInfo(start, end, softWrap, atLineStart ? line + span.LineDeltaBefore : null);

            if (softWrap)
            {
                atLineStart = false;
            }
            else
            {
                line++;
                atLineStart = true;
            }

            start = end;
        }
    }

    /// <summary>
    /// One place the document has been edited: the rows it re-derived, what they did to the
    /// document's byte, row and line counts, and the running totals of every span before it.
    ///
    /// Everything it holds is either relative to its own start or a property of the original
    /// index, so a span is untouched by edits anywhere else - which is the whole point of there
    /// being more than one.
    /// </summary>
    private sealed class DirtySpan
    {
        /// <summary>Anchor bucket in the original index this span begins at.</summary>
        public int OriginalAnchor;

        /// <summary>Where that anchor's row starts, in the original bytes.</summary>
        public long OriginalStartOffset;

        /// <summary>The original row this span's re-derivation rejoined the original index at.</summary>
        public int ConvergedOriginalRow;

        /// <summary>
        /// One marker every <see cref="RawSegmentIndex.AnchorStride"/> rows of this span, with
        /// offsets relative to <see cref="StartOffset"/> and line numbers in the original index's
        /// numbering - so a span is untouched by anything that changes before it.
        /// </summary>
        public readonly List<SpanAnchor> Anchors = new();

        /// <summary>Rows this span covers. The anchors describe every 64th one of them.</summary>
        public int RowsHeld;

        /// <summary>Bytes this span covers, relative to <see cref="StartOffset"/>.</summary>
        public long Extent;

        /// <summary>How far past this span's start every edit in it reaches, in edited bytes.</summary>
        public long EditReach;

        public long ByteDelta;
        public int RowDelta;
        public int LineDelta;

        public long ByteDeltaBefore;
        public int RowDeltaBefore;
        public int LineDeltaBefore;

        public long ByteDeltaThrough => ByteDeltaBefore + ByteDelta;
        public int RowDeltaThrough => RowDeltaBefore + RowDelta;
        public int LineDeltaThrough => LineDeltaBefore + LineDelta;

        public int OriginalStartRow => OriginalAnchor * RawSegmentIndex.AnchorStride;

        /// <summary>First row of this span, in edited-document row space.</summary>
        public int StartRow => OriginalStartRow + RowDeltaBefore;

        /// <summary>First byte of this span, in edited-document byte space.</summary>
        public long StartOffset => OriginalStartOffset + ByteDeltaBefore;

        /// <summary>Exclusive end of this span, in edited-document byte space.</summary>
        public long EndOffset => StartOffset + Extent;

        /// <summary>Where a bucket's walk starts, in document coordinates.</summary>
        public (long Start, bool AtLineStart, int LineNumber) AnchorState(int bucket)
        {
            var anchor = Anchors[bucket];
            return (StartOffset + anchor.Offset, anchor.AtLineStart, anchor.LineNumber);
        }

        /// <summary>The bucket whose walk reaches <paramref name="relative"/>: the last anchor
        /// starting at or before it.</summary>
        public int BucketContaining(long relative)
        {
            int lo = 0, hi = Anchors.Count - 1, found = 0;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (Anchors[mid].Offset <= relative)
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
