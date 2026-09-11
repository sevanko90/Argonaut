using System;
using System.Collections.Generic;

namespace Argonaut.Features.Raw;

/// <summary>What an undo or redo did, so the caller can bring the row index and caret with it.</summary>
/// <param name="Extent">The change in logical byte terms, to hand to
/// <see cref="RawEditedRowIndex.ApplyEdit"/>.</param>
/// <param name="Caret">Where the caret belongs afterwards.</param>
public readonly record struct RawUndoStep(RawEditExtent Extent, long Caret);

/// <summary>
/// Undo and redo for the raw editor.
///
/// A step is a <i>snapshot of the piece list</i>, not an inverted edit. That sounds wasteful and
/// is the opposite: a piece is a few dozen bytes and the list is bounded by the row index's
/// rebuild threshold, so a deep stack costs kilobytes - whereas inverting a delete means keeping
/// the deleted <i>bytes</i> alive, and selecting a multi-GB document and pressing delete would
/// have to retain all of it to stay undoable. Scratch is append-only and never reclaimed, so an
/// undone edit's bytes are still there when redo points at them again.
///
/// Typing coalesces: consecutive single insertions that continue where the last one ended are one
/// undo unit, so undo does not walk back a character at a time. A deletion, a paste, a caret jump
/// or an explicit <see cref="BreakRun"/> ends the run.
/// </summary>
public sealed class RawEditJournal
{
    /// <summary>An insertion longer than this is a paste, and gets its own undo step.</summary>
    private const long MaxCoalescedInsert = 4;

    private readonly RawPieceTable document;
    private readonly List<Entry> entries = new();

    /// <summary>The document as it stood before the first recorded edit.</summary>
    private readonly object baseline;

    private int applied;
    private bool runOpen;
    private long runEnd;

    public RawEditJournal(RawPieceTable document)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.document = document;
        this.baseline = document.Snapshot();
    }

    public bool CanUndo => this.applied > 0;

    public bool CanRedo => this.applied < this.entries.Count;

    /// <summary>Undo steps currently held.</summary>
    public int Depth => this.entries.Count;

    /// <summary>
    /// Records an edit that has <b>already been applied</b> to the document. The caret positions
    /// are what undo and redo restore: where the caret was before the edit, and where it ends up
    /// after it.
    /// </summary>
    public void Record(RawEditExtent extent, long caretBefore, long caretAfter)
    {
        // Anything recorded after an undo discards the redo tail - the future it described no
        // longer follows from the present.
        if (CanRedo)
        {
            this.entries.RemoveRange(this.applied, this.entries.Count - this.applied);
            this.runOpen = false;
        }

        bool continuesRun =
            this.runOpen &&
            this.entries.Count > 0 &&
            extent.BytesRemoved == 0 &&
            extent.BytesInserted > 0 &&
            extent.BytesInserted <= MaxCoalescedInsert &&
            extent.Offset == this.runEnd;

        if (continuesRun)
        {
            // Widen the open step rather than adding one: its "before" state and caret stay, and
            // its "after" state becomes the document as it now stands.
            var open = this.entries[^1];
            this.entries[^1] = open with
            {
                After = this.document.Snapshot(),
                CaretAfter = caretAfter,
                Extent = new RawEditExtent(
                    Math.Min(open.Extent.Offset, extent.Offset),
                    open.Extent.BytesRemoved,
                    open.Extent.BytesInserted + extent.BytesInserted)
            };
        }
        else
        {
            object before = this.entries.Count > 0 ? this.entries[^1].After : this.baseline;
            this.entries.Add(new Entry(before, this.document.Snapshot(), extent, caretBefore, caretAfter));
            this.applied = this.entries.Count;
        }

        this.runOpen = extent.BytesRemoved == 0 && extent.BytesInserted > 0 && extent.BytesInserted <= MaxCoalescedInsert;
        this.runEnd = extent.Offset + extent.BytesInserted;
    }

    /// <summary>Ends the current typing run, so the next insertion starts a new undo step. Called
    /// when the caret moves for any reason other than the typing itself.</summary>
    public void BreakRun() => this.runOpen = false;

    /// <summary>
    /// Reverses the newest applied step, or null when there is nothing to undo. The document is
    /// restored here; the returned step is what the row index and caret need to follow.
    /// </summary>
    public RawUndoStep? Undo()
    {
        if (!CanUndo)
            return null;

        var entry = this.entries[this.applied - 1];
        this.document.Restore(entry.Before);
        this.applied--;
        this.runOpen = false;

        // Undoing swaps the edit's two sides: what it inserted is now removed, and vice versa.
        var reversed = new RawEditExtent(entry.Extent.Offset, entry.Extent.BytesInserted, entry.Extent.BytesRemoved);
        return new RawUndoStep(reversed, entry.CaretBefore);
    }

    /// <summary>Re-applies the step undo last reversed, or null when there is nothing to redo.</summary>
    public RawUndoStep? Redo()
    {
        if (!CanRedo)
            return null;

        var entry = this.entries[this.applied];
        this.document.Restore(entry.After);
        this.applied++;
        this.runOpen = false;

        return new RawUndoStep(entry.Extent, entry.CaretAfter);
    }

    private sealed record Entry(object Before, object After, RawEditExtent Extent, long CaretBefore, long CaretAfter);
}
