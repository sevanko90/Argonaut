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
/// The document is therefore three spans:
///
///   [0, dirty)                rows straight from the original index, untouched
///   [dirty, dirty + derived)  re-derived over the edited bytes, held in full
///   [dirty + derived, end)    original rows again, displaced by constant deltas
///
/// Edits coalesce into that single dirty span rather than accumulating a list of shifts: a
/// second edit near the first widens it by nothing, and two edits far apart make it wide enough
/// to trip <see cref="NeedsRebuild"/>, which is the honest signal that a full background
/// re-index over the piece table is now cheaper than carrying the difference. Real editing is
/// local, so the common case never gets near it.
///
/// Re-derivation walks two streams from the same anchor - one over the original bytes, one over
/// the edited document - and stops when they provably re-converge: both past every edit, offsets
/// differing by exactly the document's total byte delta, and agreeing on whether the row starts a
/// line. Past the last edit the bytes are identical, and a row's extent depends only on the bytes
/// from its start, so from that point the streams cannot diverge again. Nothing here guesses that
/// re-flow "usually" settles - a forced break backs off up to 3 bytes to avoid splitting a UTF-8
/// character, so a single inserted byte can shift every later break in a long line.
///
/// Not thread-safe, and deliberately requires a completed scan (see the constructor).
/// </summary>
public sealed class RawEditedRowIndex : IRawRowIndex
{
    /// <summary>
    /// How many rows may be re-derived before a full re-index is the better deal. 64 anchor
    /// buckets - the same order as the bounded rescan a plain lookup already does, times a
    /// generous factor, and about 2MB of rescan at the widest wrap.
    /// </summary>
    internal const int MaxDerivedRows = 64 * RawSegmentIndex.AnchorStride;

    private readonly RawSegmentIndex original;
    private readonly IByteSource originalBytes;
    private readonly RawPieceTable document;
    private readonly int wrapWidth;
    private readonly long originalLength;

    private readonly List<RawRowInfo> derived = new();

    private bool hasEdits;
    private int dirtyAnchor;          // first original anchor bucket the dirty span covers
    private long dirtyStartOffset;    // where that anchor's row starts (identical in both spaces)
    private int convergedOriginalRow; // original row the re-derivation rejoined at
    private long editHighWater;       // current-space offset past every edit so far
    private int rowDelta;
    private int lineDelta;

    /// <param name="original">Index over <paramref name="originalBytes"/>. Must be complete:
    /// editing is gated on a finished scan precisely so a lock-free append log is never read
    /// while a mutating coordinate system is layered on top of it.</param>
    /// <param name="originalBytes">The bytes <paramref name="original"/> indexed.</param>
    /// <param name="document">The edited document, over the same original bytes.</param>
    public RawEditedRowIndex(RawSegmentIndex original, IByteSource originalBytes, RawPieceTable document)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(originalBytes);
        ArgumentNullException.ThrowIfNull(document);
        if (!original.IsComplete)
            throw new ArgumentException("The scan must have finished before edits are layered on it.", nameof(original));

        this.original = original;
        this.originalBytes = originalBytes;
        this.document = document;
        this.wrapWidth = original.WrapWidth;
        this.originalLength = originalBytes.Length;
    }

    /// <summary>Rows in the edited document.</summary>
    public int RowCount => this.original.RowCount + this.rowDelta;

    /// <summary>
    /// True once the dirty span has grown past <see cref="MaxDerivedRows"/>. Row lookups stay
    /// correct, but the owner should start a background re-index over the piece table and swap
    /// this instance out for one over the result.
    /// </summary>
    public bool NeedsRebuild { get; private set; }

    /// <summary>Rows currently re-derived rather than read from the original index.</summary>
    internal int DerivedRowCount => this.derived.Count;

    /// <summary>Total byte difference between the edited document and the original bytes.</summary>
    private long ByteDelta => this.document.Length - this.originalLength;

    /// <summary>First row of the dirty span, in edited-document row space.</summary>
    private int DirtyStartRow => this.hasEdits ? this.dirtyAnchor * RawSegmentIndex.AnchorStride : this.original.RowCount;

    /// <summary>
    /// Folds one edit in and re-derives whatever it disturbed. Cost is bounded by the dirty
    /// span, not by the file.
    /// </summary>
    public void ApplyEdit(RawEditExtent extent)
    {
        // Track an offset known to be past every edit, so re-convergence is only ever declared
        // where the bytes really are identical again. An edit at or before the existing mark
        // displaces it by its own delta.
        if (this.hasEdits && extent.Offset <= this.editHighWater)
            this.editHighWater += extent.ByteDelta;

        this.editHighWater = Math.Max(this.editHighWater, extent.Offset + extent.BytesInserted);

        int anchorForEdit = AnchorContaining(OriginalOffsetOf(extent.Offset));
        this.dirtyAnchor = this.hasEdits ? Math.Min(this.dirtyAnchor, anchorForEdit) : anchorForEdit;
        this.hasEdits = true;

        Rederive();
    }

    public RawRowInfo GetRowInfo(int rowIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(rowIndex, RowCount);

        if (!this.hasEdits || rowIndex < DirtyStartRow)
            return this.original.GetRowInfo(rowIndex);

        int withinDirty = rowIndex - DirtyStartRow;
        if (withinDirty < this.derived.Count)
            return this.derived[withinDirty];

        return Displace(this.original.GetRowInfo(rowIndex - this.rowDelta));
    }

    /// <summary>
    /// The row containing <paramref name="offset"/> in the edited document, or null when the
    /// offset is past its end.
    /// </summary>
    public int? RowForOffset(long offset)
    {
        if (offset < 0 || offset >= this.document.Length)
            return null;

        if (!this.hasEdits || offset < this.dirtyStartOffset)
            return this.original.RowForOffset(offset);

        // Inside the re-derived span: binary search the rows we are holding.
        if (this.derived.Count > 0 && offset < this.derived[^1].End)
        {
            int lo = 0, hi = this.derived.Count - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (offset < this.derived[mid].End)
                    hi = mid;
                else
                    lo = mid + 1;
            }

            return DirtyStartRow + lo;
        }

        int? originalRow = this.original.RowForOffset(offset - ByteDelta);
        return originalRow is null ? null : originalRow.Value + this.rowDelta;
    }

    /// <summary>Where <paramref name="currentOffset"/> sits in the original bytes. Only called
    /// for offsets outside the dirty span, where the two spaces differ by a constant.</summary>
    private long OriginalOffsetOf(long currentOffset)
        => !this.hasEdits || currentOffset < this.dirtyStartOffset
            ? currentOffset
            : Math.Clamp(currentOffset - ByteDelta, 0, Math.Max(this.originalLength - 1, 0));

    private int AnchorContaining(long originalOffset)
    {
        int? row = this.original.RowForOffset(originalOffset);
        int rowIndex = row ?? Math.Max(this.original.RowCount - 1, 0);
        return rowIndex / RawSegmentIndex.AnchorStride;
    }

    private RawRowInfo Displace(RawRowInfo info)
        => new(info.Start + ByteDelta,
               info.End + ByteDelta,
               info.IsSoftWrapped,
               info.LineNumber is int line ? line + this.lineDelta : null);

    /// <summary>
    /// Walks the original bytes and the edited document forward from the dirty anchor in
    /// lockstep, holding the edited document's rows, until the two provably re-converge.
    /// </summary>
    private void Rederive()
    {
        this.derived.Clear();

        // An empty original has no anchors at all, but can still be typed into.
        var anchor = this.original.RowCount == 0
            ? (Start: 0L, AtLineStart: true, LineNumber: 1)
            : this.original.AnchorAt(this.dirtyAnchor);
        this.dirtyStartOffset = anchor.Start;

        long byteDelta = ByteDelta;
        long documentLength = this.document.Length;

        // The edited stream, which is what we keep.
        long currentStart = anchor.Start;
        bool currentAtLineStart = anchor.AtLineStart;
        int currentLine = anchor.LineNumber;

        // The original stream, walked only to recognise where the two rejoin.
        long originalStart = anchor.Start;
        bool originalAtLineStart = anchor.AtLineStart;
        int originalLine = anchor.LineNumber;
        int originalRow = this.dirtyAnchor * RawSegmentIndex.AnchorStride;

        while (currentStart < documentLength)
        {
            var (end, softWrap) = RawRowBoundary.Next(this.document, this.wrapWidth, currentStart);
            this.derived.Add(new RawRowInfo(currentStart, end, softWrap, currentAtLineStart ? currentLine : null));

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
            while (originalStart < this.originalLength && originalStart + byteDelta < currentStart)
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
                currentStart >= this.editHighWater &&
                originalStart < this.originalLength &&
                originalStart + byteDelta == currentStart &&
                originalAtLineStart == currentAtLineStart;

            if (converged)
            {
                this.convergedOriginalRow = originalRow;
                this.rowDelta = DirtyStartRow + this.derived.Count - originalRow;
                this.lineDelta = currentLine - originalLine;
                NeedsRebuild = this.derived.Count > MaxDerivedRows;
                return;
            }

            if (this.derived.Count > MaxDerivedRows)
                NeedsRebuild = true;
        }

        // Walked to the end of the document without rejoining: the dirty span runs to the end,
        // and there is no displaced tail to describe.
        this.convergedOriginalRow = this.original.RowCount;
        this.rowDelta = DirtyStartRow + this.derived.Count - this.original.RowCount;
        this.lineDelta = 0;
    }
}
