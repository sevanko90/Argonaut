using System;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>
/// Owns where the caret is and what is selected, and performs every movement that can be
/// expressed in byte offsets.
///
/// Vertical movement and click-to-place are deliberately <i>not</i> here. Both are inherently
/// pixel questions - "the same column, one row up" means the same x, and the content font may be
/// proportional - so they belong to whatever holds the text layouts, which is
/// <see cref="RawTextSurface"/>. Everything else is byte arithmetic over the row index and
/// therefore testable with no UI at all, which is most of the behaviour worth testing.
/// </summary>
public sealed class RawCaretController
{
    private readonly IRawRowIndex rows;
    private readonly IByteSource source;

    public RawCaretController(IRawRowIndex rows, IByteSource source)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(source);
        this.rows = rows;
        this.source = source;
    }

    /// <summary>Where the caret is, and which side of a wrap boundary it draws on.</summary>
    public RawCaret Caret { get; private set; }

    /// <summary>What is selected. Empty when the caret is just sitting somewhere.</summary>
    public RawSelection Selection { get; private set; }

    /// <summary>Raised whenever the caret or the selection moved, so a view can redraw.</summary>
    public event EventHandler? Moved;

    /// <summary>Puts the caret at <paramref name="offset"/>, snapped to a legal position, and
    /// collapses any selection there.</summary>
    public void PlaceAt(long offset, CaretAffinity affinity = CaretAffinity.Downstream)
    {
        long snapped = Snap(offset);
        Apply(new RawCaret(snapped, affinity), RawSelection.At(snapped));
    }

    /// <summary>Moves the caret to <paramref name="offset"/> keeping the selection anchored -
    /// what a shift-click or a drag does.</summary>
    public void ExtendTo(long offset, CaretAffinity affinity = CaretAffinity.Downstream)
    {
        long snapped = Snap(offset);
        Apply(new RawCaret(snapped, affinity), Selection with { Active = snapped });
    }

    /// <summary>
    /// One character left. With a selection and <paramref name="extend"/> false this collapses to
    /// the selection's start rather than moving, which is what every editor does and what stops a
    /// user losing their place when they dismiss a selection.
    /// </summary>
    public void MoveLeft(bool extend)
    {
        if (!extend && !Selection.IsEmpty)
        {
            PlaceAt(Selection.Start);
            return;
        }

        long target = RawCaretStops.Previous(this.rows, this.source, Caret.Offset);
        MoveTo(target, extend, AffinityAfterMovingLeft(target));
    }

    /// <summary>One character right; collapses a selection to its end when not extending.</summary>
    public void MoveRight(bool extend)
    {
        if (!extend && !Selection.IsEmpty)
        {
            PlaceAt(Selection.End);
            return;
        }

        long target = RawCaretStops.Next(this.rows, this.source, Caret.Offset);
        MoveTo(target, extend, CaretAffinity.Downstream);
    }

    /// <summary>To the start of the row the caret is on.</summary>
    public void MoveToRowStart(bool extend)
    {
        if (CurrentRow() is not { } row)
            return;

        MoveTo(row.Info.Start, extend, CaretAffinity.Downstream);
    }

    /// <summary>
    /// To the end of the row's drawn text - before any newline bytes, which are inside the row's
    /// range but hold no caret.
    /// </summary>
    public void MoveToRowEnd(bool extend)
    {
        if (CurrentRow() is not { } row)
            return;

        // Upstream: on a soft-wrapped row this offset is also the next row's start, and End must
        // leave the caret visibly at the end of the row the user pressed it on.
        MoveTo(row.Info.Start + row.Decoded.DisplayByteLength, extend, CaretAffinity.Upstream);
    }

    public void MoveToDocumentStart(bool extend) => MoveTo(0, extend, CaretAffinity.Downstream);

    public void MoveToDocumentEnd(bool extend) => MoveTo(this.source.Length, extend, CaretAffinity.Upstream);

    /// <summary>
    /// Selects the word around <paramref name="offset"/> - what a double-click does. False when
    /// <see cref="RawWordStops"/> refused because the run is longer than a word can be; the caret
    /// and selection are left alone so the caller can say so instead.
    /// </summary>
    public bool SelectWordAt(long offset)
    {
        if (RawWordStops.WordAt(this.rows, this.source, offset) is not { } word)
            return false;

        var (start, end) = word;

        // Upstream so a word ending exactly on a wrap boundary keeps the caret on the row the
        // word is drawn on, rather than jumping to the start of the next one.
        Apply(new RawCaret(end, CaretAffinity.Upstream), new RawSelection(start, end));
        return true;
    }

    /// <summary>Selects everything. Two offsets, so the size of the document is irrelevant.</summary>
    public void SelectAll()
        => Apply(new RawCaret(this.source.Length, CaretAffinity.Upstream), new RawSelection(0, this.source.Length));

    /// <summary>Drops the selection, leaving the caret where it is.</summary>
    public void ClearSelection()
    {
        if (Selection.IsEmpty)
            return;

        Apply(Caret, RawSelection.At(Caret.Offset));
    }

    /// <summary>
    /// Re-snaps the caret and selection after the document changed underneath them - a wrap-width
    /// change, or a scan publishing rows that were not there before. The offsets stay put; what
    /// can change is whether they are still legal positions.
    /// </summary>
    public void Revalidate()
    {
        long caret = Snap(Caret.Offset);
        long anchor = Snap(Selection.Anchor);
        long active = Snap(Selection.Active);
        Apply(Caret with { Offset = caret }, new RawSelection(anchor, active));
    }

    /// <summary>Used by the surface, which resolves a click to an offset from its text layouts.</summary>
    internal void MoveTo(long offset, bool extend, CaretAffinity affinity)
    {
        long snapped = Snap(offset);
        var caret = new RawCaret(snapped, affinity);
        Apply(caret, extend ? Selection with { Active = snapped } : RawSelection.At(snapped));
    }

    private void Apply(RawCaret caret, RawSelection selection)
    {
        if (caret == Caret && selection == Selection)
            return;

        Caret = caret;
        Selection = selection;
        Moved?.Invoke(this, EventArgs.Empty);
    }

    private long Snap(long offset)
    {
        // With no rows to snap against - which is the state a freshly restarted scan is in for
        // its first few milliseconds - keep the offset rather than collapsing it to zero. A
        // wrap-width change restores the caret the instant it swaps the index, and throwing the
        // position away because the replacement scan had not published yet is a race that only
        // shows up under load.
        if (this.rows.RowCount == 0)
            return Math.Clamp(offset, 0, this.source.Length);

        return RawCaretStops.Snap(this.rows, this.source, offset, CaretSnap.Backward);
    }

    /// <summary>
    /// Moving left across a soft-wrap boundary should land the caret at the visible end of the
    /// row above, not at the start of the row it just left.
    /// </summary>
    private CaretAffinity AffinityAfterMovingLeft(long target)
    {
        int? rowIndex = this.rows.RowForOffset(target);
        if (rowIndex is null || rowIndex.Value == 0)
            return CaretAffinity.Downstream;

        var info = this.rows.GetRowInfo(rowIndex.Value);
        return target == info.Start ? CaretAffinity.Upstream : CaretAffinity.Downstream;
    }

    private (RawRowInfo Info, RawDecodedRow Decoded)? CurrentRow()
    {
        int? rowIndex = this.rows.RowForOffset(Caret.Offset)
                        ?? (this.rows.RowCount > 0 ? this.rows.RowCount - 1 : null);

        if (rowIndex is null)
            return null;

        var info = this.rows.GetRowInfo(rowIndex.Value);
        return (info, RawRowDecoder.Decode(this.source, info.Start, info.End, info.IsSoftWrapped));
    }
}
