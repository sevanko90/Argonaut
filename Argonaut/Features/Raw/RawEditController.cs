using System;
using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Raw;

/// <summary>What an attempted edit did, so the caller can say so rather than look inert.</summary>
public enum RawEditOutcome
{
    /// <summary>The document changed.</summary>
    Applied,

    /// <summary>There was nothing to do - backspace at offset 0, an empty paste.</summary>
    NothingToDo,

    /// <summary>
    /// Refused because the row index has run out of room to track another separate place. See
    /// <see cref="RawEditedRowIndex.CanAbsorbEditAt"/>: each place edited costs the index a span
    /// of re-derived rows, and once they exhaust its budget the honest answer is a background
    /// re-index over the piece table rather than opening more. Distance between edits is not
    /// what this is about - the number of distinct places is. Editing where you already have is
    /// still allowed.
    /// </summary>
    NoRoomForAnotherEditSite
}

/// <summary>
/// Editing for the raw viewer: what a keystroke does to the document.
///
/// It owns the four pieces the byte layer already provides and nothing else - the
/// <see cref="RawPieceTable"/> holding the edits, the <see cref="RawEditedRowIndex"/> that
/// re-derives the rows they disturbed, the <see cref="RawEditJournal"/> that makes them
/// undoable, and the <see cref="RawCaretController"/> that moves over the result. Creating all
/// four here rather than accepting them is deliberate: three of them are only meaningful over
/// exactly the fourth's coordinate space, and handing them in separately is an invitation to
/// pair a caret with the wrong row index.
///
/// <b>Nothing here touches the file on disk.</b> The document reads as the edited bytes; the
/// original mapping is never written. Saving is a separate, streaming rewrite - see
/// docs/save-plan.md - and until it exists an edited document is an in-memory
/// difference from what is on disk, which is what <see cref="IsDirty"/> says.
///
/// UI thread only: <see cref="RawPieceTable"/> is not thread-safe, and editing is gated on the
/// background scan having finished precisely so no scan is reading while it mutates.
/// </summary>
public sealed class RawEditController
{
    /// <summary>The line ending Enter inserts. Deliberately not the document's prevailing one:
    /// this is the viewer that exists for files whose contents cannot be assumed about, and
    /// guessing a convention from bytes that may be half binary would be a worse surprise than a
    /// consistent LF the user can see in the gutter.</summary>
    private static readonly byte[] NewLine = { (byte)'\n' };

    private readonly RawEditJournal journal;

    /// <summary>The file's length, kept only so the inspector can report what the edits changed
    /// it by; the piece table deliberately does not remember it.</summary>
    private readonly long originalLength;

    /// <summary>True while an edit of ours is moving the caret, so the caret's own movement
    /// notification does not end the typing run the edit is part of.</summary>
    private bool movingForOwnEdit;

    /// <param name="scan">Completed index over <paramref name="originalBytes"/>.</param>
    /// <param name="originalBytes">The document's bytes as they are on disk.</param>
    public RawEditController(RawSegmentIndex scan, IByteSource originalBytes)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(originalBytes);

        this.originalLength = originalBytes.AvailableLength;
        Document = new RawPieceTable(originalBytes);
        RowIndex = new RawEditedRowIndex(scan, Document);
        Caret = new RawCaretController(RowIndex, Document);
        this.journal = new RawEditJournal(Document);

        // A caret moved by anything other than an edit ends the undo run, so undoing after
        // clicking elsewhere does not also unwind what was typed before the click.
        Caret.Moved += (_, _) =>
        {
            if (!this.movingForOwnEdit)
                this.journal.BreakRun();
        };
    }

    /// <summary>The edited document. What every reader in the view reads through while editing.</summary>
    public RawPieceTable Document { get; }

    /// <summary>Rows over <see cref="Document"/>.</summary>
    public RawEditedRowIndex RowIndex { get; }

    /// <summary>The caret, over <see cref="Document"/> and <see cref="RowIndex"/>.</summary>
    public RawCaretController Caret { get; }

    /// <summary>Raised after the document's bytes changed, before the caret is moved to follow
    /// them - so a listener that caches rows drops them while the caret is still where it was.</summary>
    public event EventHandler? Changed;

    /// <summary>True once the document reads differently from the file on disk.</summary>
    public bool IsDirty => !Document.IsUnedited;

    public bool CanUndo => this.journal.CanUndo;

    public bool CanRedo => this.journal.CanRedo;

    /// <summary>
    /// True once the re-derived spans together hold more than <see cref="RawEditedRowIndex"/> is
    /// willing to, so edits in places not already being edited are refused. The real answer is a
    /// background re-index over the piece table (roadmap: "More than one dirty span in
    /// RawEditedRowIndex").
    /// </summary>
    public bool NeedsRebuild => RowIndex.NeedsRebuild;

    /// <summary>
    /// What the editor's internals look like right now, for the debug inspector. Copies
    /// everything it reports and reads no bytes, so it is safe to take on every keystroke and
    /// safe to keep after the document has moved on.
    /// </summary>
    /// <param name="maxPieces">How many pieces to describe before summarising the rest.</param>
    public RawEditSnapshot Describe(int maxPieces = 500)
    {
        var pieces = Document.DescribePieces(maxPieces, out int omitted);
        var selection = Caret.Selection;

        return new RawEditSnapshot(
            Document.AvailableLength,
            this.originalLength,
            Document.IsUnedited,
            Document.PieceCount,
            Document.ScratchChunkCount,
            Document.ScratchBytesUsed,
            pieces,
            omitted,
            RowIndex.RowCount,
            RowIndex.OriginalRowCount,
            RowIndex.SpanCount,
            RowIndex.TotalDerivedRows,
            RowIndex.HeldLines,
            RawEditedRowIndex.MaxHeldLines,
            RowIndex.BytesScannedInLastEdit,
            RowIndex.LinesRebuiltInLastEdit,
            RowIndex.NeedsRebuild,
            RowIndex.DescribeSpans(),
            this.journal.Depth,
            CanUndo,
            CanRedo,
            Caret.Caret.Offset,
            selection.Start,
            selection.End);
    }

    /// <summary>Whether <paramref name="scan"/> is finished, which is what editing waits for: the
    /// append log is read lock-free because nothing already written ever changes, and a row index
    /// mutated on the UI thread while the scan still appended to it would end that.</summary>
    public static bool CanEdit(RawSegmentIndex scan) => scan.AllItemsPublished;

    /// <summary>Types <paramref name="text"/> at the caret, replacing the selection if there is
    /// one.</summary>
    public RawEditOutcome Type(string text)
    {
        if (string.IsNullOrEmpty(text))
            return RawEditOutcome.NothingToDo;

        return Insert(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Inserts a line break at the caret, replacing the selection if there is one.</summary>
    public RawEditOutcome InsertNewLine() => Insert(NewLine);

    /// <summary>
    /// Inserts <paramref name="bytes"/> at the caret, replacing the selection if there is one.
    /// An empty <paramref name="bytes"/> with a selection is how a deletion of that selection is
    /// expressed, so that both reach the piece table as one undoable replacement.
    /// </summary>
    public RawEditOutcome Insert(ReadOnlySpan<byte> bytes)
    {
        var selection = Caret.Selection;
        long start = selection.IsEmpty ? Caret.Caret.Offset : selection.Start;
        long removed = selection.Length;

        if (bytes.IsEmpty && removed == 0)
            return RawEditOutcome.NothingToDo;

        return Apply(start, removed, bytes);
    }

    /// <summary>
    /// Backspace. With no selection this removes the character before the caret - <i>character</i>
    /// rather than byte, so a multi-byte character or a run of invalid bytes that drew as one
    /// U+FFFD goes in one press, which is the point of <see cref="RawCaretStops"/> being the
    /// thing that decides. At the start of a row the previous stop is before the line ending, so
    /// backspace there joins the two lines.
    /// </summary>
    public RawEditOutcome DeleteBackward()
    {
        if (!Caret.Selection.IsEmpty)
            return Insert(ReadOnlySpan<byte>.Empty);

        long end = Caret.Caret.Offset;
        if (end <= 0)
            return RawEditOutcome.NothingToDo;

        long start = RawCaretStops.Previous(RowIndex, Document, end);
        return start >= end ? RawEditOutcome.NothingToDo : Apply(start, end - start, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Delete forward. At the end of a row's drawn text the next stop is past the line
    /// ending, so this joins the two lines.</summary>
    public RawEditOutcome DeleteForward()
    {
        if (!Caret.Selection.IsEmpty)
            return Insert(ReadOnlySpan<byte>.Empty);

        long start = Caret.Caret.Offset;
        if (start >= Document.AvailableLength)
            return RawEditOutcome.NothingToDo;

        long end = RawCaretStops.Next(RowIndex, Document, start);
        return end <= start ? RawEditOutcome.NothingToDo : Apply(start, end - start, ReadOnlySpan<byte>.Empty);
    }

    /// <summary>Reverses the newest edit. Never refused for distance: undo can only shrink the
    /// span the row index is holding, never widen it past where it has already been.</summary>
    public RawEditOutcome Undo() => Follow(this.journal.Undo());

    /// <summary>Re-applies the edit undo last reversed.</summary>
    public RawEditOutcome Redo() => Follow(this.journal.Redo());

    /// <summary>
    /// The one path every edit takes: check what the row index can absorb, change the document,
    /// fold the change into the rows, record it for undo, then move the caret to follow it.
    ///
    /// The order is load-bearing at both ends. <see cref="RawEditedRowIndex.ApplyEdit"/> must run
    /// before the caret moves, because the caret snaps to a legal position by asking the row
    /// index where the rows are; and <see cref="Changed"/> must fire before the caret moves, so
    /// whatever caches rows has dropped them by the time the caret's own notification has it
    /// drawing.
    /// </summary>
    private RawEditOutcome Apply(long start, long removed, ReadOnlySpan<byte> inserted)
    {
        if (!RowIndex.CanAbsorbEditAt(start))
            return RawEditOutcome.NoRoomForAnotherEditSite;

        long caretBefore = Caret.Caret.Offset;
        var extent = removed > 0
            ? Document.Replace(start, removed, inserted)
            : Document.Insert(start, inserted);

        RowIndex.ApplyEdit(extent);
        this.journal.Record(extent, caretBefore, start + inserted.Length);

        Changed?.Invoke(this, EventArgs.Empty);
        MoveCaretForOwnEdit(start + inserted.Length);
        return RawEditOutcome.Applied;
    }

    private RawEditOutcome Follow(RawUndoStep? step)
    {
        if (step is not { } undone)
            return RawEditOutcome.NothingToDo;

        RowIndex.ApplyEdit(undone.Extent);
        Changed?.Invoke(this, EventArgs.Empty);
        MoveCaretForOwnEdit(undone.Caret);
        return RawEditOutcome.Applied;
    }

    /// <summary>
    /// Puts the caret where the edit left it. <see cref="RawCaretController"/> raises nothing
    /// when the offset has not moved - deleting forward leaves it exactly where it was - so
    /// listeners redraw off <see cref="Changed"/> rather than off the caret.
    /// </summary>
    private void MoveCaretForOwnEdit(long offset)
    {
        this.movingForOwnEdit = true;
        try
        {
            Caret.PlaceAt(offset);
        }
        finally
        {
            this.movingForOwnEdit = false;
        }
    }
}
