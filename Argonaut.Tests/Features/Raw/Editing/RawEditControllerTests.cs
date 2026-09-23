using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Editing;
using Argonaut.Features.Raw.Rows;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw.Editing;

/// <summary>
/// What a keystroke does to the document, with no UI at all - the rules
/// <see cref="RawEditInputTests"/> then proves are actually wired to the keyboard.
///
/// The oracle throughout is the document's own bytes read back through the piece table, because
/// that is what every reader in the view sees; <see cref="RawEditedRowIndexTests"/> already holds
/// the row index to a from-scratch re-index, so nothing here re-litigates that.
/// </summary>
public class RawEditControllerTests
{
    private const int WrapWidth = 80;

    private static RawEditController Editing(string content)
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes(content));
        var scan = RawSegmentIndex.StartIndexing(source, WrapWidth);
        scan.IndexingTask.GetAwaiter().GetResult();

        return new RawEditController(scan, source);
    }

    private static string TextOf(RawEditController editor)
    {
        var bytes = new byte[editor.Document.AvailableLength];
        editor.Document.CopyTo(0, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    [Fact]
    public void Type_InsertsAtTheCaretAndMovesItPastWhatWasTyped()
    {
        var editor = Editing("hello world\n");
        editor.Caret.PlaceAt(5);

        Assert.Equal(RawEditOutcome.Applied, editor.Type(","));

        Assert.Equal("hello, world\n", TextOf(editor));
        Assert.Equal(6, editor.Caret.Caret.Offset);
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void Type_WithASelection_ReplacesIt()
    {
        var editor = Editing("hello world\n");
        editor.Caret.PlaceAt(6);
        editor.Caret.ExtendTo(11);

        editor.Type("there");

        Assert.Equal("hello there\n", TextOf(editor));
        Assert.Equal(11, editor.Caret.Caret.Offset);
        Assert.True(editor.Caret.Selection.IsEmpty);
    }

    [Fact]
    public void Type_MultiByteCharacter_InsertsItsUtf8Bytes()
    {
        var editor = Editing("ab\n");
        editor.Caret.PlaceAt(1);

        editor.Type("é");

        Assert.Equal("aéb\n", TextOf(editor));

        // Two bytes, so the caret is two bytes on - the whole reason the caret is a byte offset
        // and character positions are derived rather than stored.
        Assert.Equal(3, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void InsertNewLine_SplitsTheLineAndTheRowsFollow()
    {
        var editor = Editing("abcdef\n");
        editor.Caret.PlaceAt(3);

        editor.InsertNewLine();

        Assert.Equal("abc\ndef\n", TextOf(editor));
        Assert.Equal(2, editor.RowIndex.RowCount);
        Assert.Equal(4, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void DeleteBackward_RemovesTheWholeCharacterBeforeTheCaret()
    {
        var editor = Editing("aéb\n");
        editor.Caret.PlaceAt(3); // just after the two bytes of 'é'

        editor.DeleteBackward();

        Assert.Equal("ab\n", TextOf(editor));
        Assert.Equal(1, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void DeleteBackward_AtTheStartOfALine_JoinsItToThePrevious()
    {
        var editor = Editing("one\ntwo\n");
        editor.Caret.PlaceAt(4); // 't' of "two"

        editor.DeleteBackward();

        Assert.Equal("onetwo\n", TextOf(editor));
        Assert.Equal(3, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void DeleteBackward_AtTheStartOfTheDocument_DoesNothing()
    {
        var editor = Editing("abc\n");
        editor.Caret.PlaceAt(0);

        Assert.Equal(RawEditOutcome.NothingToDo, editor.DeleteBackward());
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void DeleteForward_RemovesTheCharacterUnderTheCaretAndLeavesItWhereItWas()
    {
        var editor = Editing("abc\n");
        editor.Caret.PlaceAt(1);

        editor.DeleteForward();

        Assert.Equal("ac\n", TextOf(editor));
        Assert.Equal(1, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void DeleteForward_AtTheEndOfALine_JoinsTheNextLineOntoIt()
    {
        var editor = Editing("one\ntwo\n");
        editor.Caret.PlaceAt(3); // end of "one", on the newline

        editor.DeleteForward();

        Assert.Equal("onetwo\n", TextOf(editor));
    }

    [Fact]
    public void DeleteBackward_WithASelection_RemovesTheSelection()
    {
        var editor = Editing("hello world\n");
        editor.Caret.PlaceAt(5);
        editor.Caret.ExtendTo(11);

        editor.DeleteBackward();

        Assert.Equal("hello\n", TextOf(editor));
        Assert.Equal(5, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void Undo_RestoresTheDocumentAndTheCaret()
    {
        var editor = Editing("hello\n");
        editor.Caret.PlaceAt(5);
        editor.Type("!");

        Assert.Equal(RawEditOutcome.Applied, editor.Undo());

        Assert.Equal("hello\n", TextOf(editor));
        Assert.Equal(5, editor.Caret.Caret.Offset);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndo);
        Assert.True(editor.CanRedo);
    }

    [Fact]
    public void Redo_ReappliesWhatUndoReversed()
    {
        var editor = Editing("hello\n");
        editor.Caret.PlaceAt(5);
        editor.Type("!");
        editor.Undo();

        Assert.Equal(RawEditOutcome.Applied, editor.Redo());

        Assert.Equal("hello!\n", TextOf(editor));
        Assert.Equal(6, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void Undo_UnwindsARunOfTypingInOneStep()
    {
        var editor = Editing("hello\n");
        editor.Caret.PlaceAt(5);
        editor.Type("!");
        editor.Type("!");
        editor.Type("!");

        editor.Undo();

        Assert.Equal("hello\n", TextOf(editor));
        Assert.False(editor.CanUndo);
    }

    [Fact]
    public void MovingTheCaretEndsTheTypingRun()
    {
        var editor = Editing("ab\n");
        editor.Caret.PlaceAt(0);
        editor.Type("1");

        // A caret move for any reason other than the typing itself closes the undo step, so
        // undoing after clicking elsewhere does not also unwind what was typed before the click.
        editor.Caret.PlaceAt(2);
        editor.Type("2");

        editor.Undo();
        Assert.Equal("1ab\n", TextOf(editor));

        editor.Undo();
        Assert.Equal("ab\n", TextOf(editor));
    }

    [Fact]
    public void Undo_WithNothingToUndo_DoesNothing()
    {
        var editor = Editing("abc\n");
        Assert.Equal(RawEditOutcome.NothingToDo, editor.Undo());
        Assert.Equal(RawEditOutcome.NothingToDo, editor.Redo());
    }

    [Fact]
    public void Changed_FiresBeforeTheCaretMoves()
    {
        var editor = Editing("abc\n");
        editor.Caret.PlaceAt(0);

        long caretWhenChangedFired = -1;
        editor.Changed += (_, _) => caretWhenChangedFired = editor.Caret.Caret.Offset;

        editor.Type("x");

        // Whatever caches rows has to have dropped them before the caret's own notification has
        // the view drawing against them.
        Assert.Equal(0, caretWhenChangedFired);
        Assert.Equal(1, editor.Caret.Caret.Offset);
    }

    [Fact]
    public void EditsAtOppositeEndsOfALargeDocument_AreBothAccepted()
    {
        // The case that decided the row index's shape: with one dirty span this refused, because
        // the index would have had to hold every row between the two edits. Spans are per place
        // edited, so the distance between them costs nothing.
        var editor = Editing(string.Concat(Enumerable.Repeat("0123456789\n", 40_000)));

        editor.Caret.PlaceAt(0);
        Assert.Equal(RawEditOutcome.Applied, editor.Type("a"));

        editor.Caret.PlaceAt(editor.Document.AvailableLength - 1);
        Assert.Equal(RawEditOutcome.Applied, editor.Type("b"));

        Assert.Equal((byte)'a', editor.Document.GetContiguousSpan(0, 1)[0]);
        Assert.Equal(440_002, editor.Document.AvailableLength);
        Assert.False(editor.NeedsRebuild);
    }

    [Fact]
    public void EditsNearEachOther_AreAllAccepted()
    {
        var editor = Editing(string.Concat(Enumerable.Repeat("0123456789\n", 40_000)));

        for (int i = 0; i < 50; i++)
        {
            editor.Caret.PlaceAt(1000 + i);
            Assert.Equal(RawEditOutcome.Applied, editor.Type("x"));
        }

        Assert.False(editor.NeedsRebuild);
    }

    [Fact]
    public void Constructing_OverAnUnfinishedScan_Throws()
    {
        // The append log is read lock-free because nothing already written ever changes;
        // layering a mutating coordinate system over one still being appended to would end that.
        var source = new GrowingByteSource(Encoding.UTF8.GetBytes("abc\n"));
        var scan = RawSegmentIndex.StartIndexing(source, WrapWidth);

        Assert.False(RawEditController.CanEdit(scan));
        Assert.Throws<ArgumentException>(() => new RawEditController(scan, source));

        source.Seal();
        scan.IndexingTask.GetAwaiter().GetResult();
        Assert.True(RawEditController.CanEdit(scan));
    }
}
