using System.Text;
using Argonaut.Features.Raw;

namespace Argonaut.Tests;

/// <summary>
/// Undo is tested through the document it restores, not through the journal's own bookkeeping:
/// after any sequence of edits and undos, what matters is that the bytes are what they were.
/// </summary>
public class RawEditJournalTests
{
    private sealed class Editor
    {
        public Editor(string original)
        {
            Table = new RawPieceTable(new ArrayByteSource(Encoding.UTF8.GetBytes(original)));
            Journal = new RawEditJournal(Table);
        }

        public RawPieceTable Table { get; }
        public RawEditJournal Journal { get; }

        public string Text
        {
            get
            {
                var destination = new byte[Table.AvailableLength];
                Table.CopyTo(0, destination);
                return Encoding.UTF8.GetString(destination);
            }
        }

        public void Type(long offset, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Journal.Record(Table.Insert(offset, bytes), offset, offset + bytes.Length);
        }

        public void Erase(long offset, int length)
            => Journal.Record(Table.Delete(offset, length), offset + length, offset);
    }

    [Fact]
    public void NothingRecorded_HasNothingToUndoOrRedo()
    {
        var editor = new Editor("abc");

        Assert.False(editor.Journal.CanUndo);
        Assert.False(editor.Journal.CanRedo);
        Assert.Null(editor.Journal.Undo());
        Assert.Null(editor.Journal.Redo());
    }

    [Fact]
    public void UndoRestoresTheDocumentAndRedoReappliesIt()
    {
        var editor = new Editor("hello world");
        editor.Type(5, " big");
        Assert.Equal("hello big world", editor.Text);

        var undone = editor.Journal.Undo();

        Assert.Equal("hello world", editor.Text);
        Assert.NotNull(undone);
        Assert.Equal(5, undone!.Value.Caret);

        var redone = editor.Journal.Redo();

        Assert.Equal("hello big world", editor.Text);
        Assert.Equal(9, redone!.Value.Caret);
    }

    [Fact]
    public void UndoOfAnInsert_ReportsAnExtentThatRemovesWhatWasInserted()
    {
        var editor = new Editor("abc");
        editor.Type(1, "XY");

        var step = editor.Journal.Undo()!.Value;

        Assert.Equal(new RawEditExtent(1, 2, 0), step.Extent);
    }

    [Fact]
    public void UndoOfADelete_ReportsAnExtentThatPutsTheBytesBack()
    {
        var editor = new Editor("abcdef");
        editor.Erase(2, 3);
        Assert.Equal("abf", editor.Text);

        var step = editor.Journal.Undo()!.Value;

        Assert.Equal("abcdef", editor.Text);
        Assert.Equal(new RawEditExtent(2, 0, 3), step.Extent);
    }

    [Fact]
    public void ConsecutiveTyping_IsOneUndoStep()
    {
        var editor = new Editor("");
        editor.Type(0, "a");
        editor.Type(1, "b");
        editor.Type(2, "c");
        Assert.Equal("abc", editor.Text);

        Assert.Equal(1, editor.Journal.Depth);
        editor.Journal.Undo();

        Assert.Equal("", editor.Text);
        Assert.False(editor.Journal.CanUndo);
    }

    [Fact]
    public void TypingAfterACaretJump_StartsANewUndoStep()
    {
        var editor = new Editor("abcdef");
        editor.Type(0, "1");
        editor.Journal.BreakRun(); // what a click or an arrow key does
        editor.Type(4, "2");

        Assert.Equal(2, editor.Journal.Depth);

        editor.Journal.Undo();
        Assert.Equal("1abcdef", editor.Text);

        editor.Journal.Undo();
        Assert.Equal("abcdef", editor.Text);
    }

    [Fact]
    public void TypingSomewhereElse_StartsANewUndoStepWithoutBeingTold()
    {
        var editor = new Editor("abcdef");
        editor.Type(0, "1");
        editor.Type(5, "2"); // not where the last insertion ended

        Assert.Equal(2, editor.Journal.Depth);
    }

    [Fact]
    public void ADeletion_BreaksTheTypingRun()
    {
        var editor = new Editor("abc");
        editor.Type(0, "1");
        editor.Erase(0, 1);
        editor.Type(0, "2");

        Assert.Equal(3, editor.Journal.Depth);
    }

    [Fact]
    public void APaste_IsItsOwnUndoStep()
    {
        var editor = new Editor("");
        editor.Type(0, "a");
        editor.Type(1, "a large pasted block");

        Assert.Equal(2, editor.Journal.Depth);

        editor.Journal.Undo();
        Assert.Equal("a", editor.Text);
    }

    [Fact]
    public void EditingAfterAnUndo_DiscardsTheRedoTail()
    {
        var editor = new Editor("abc");
        editor.Type(0, "1");
        editor.Journal.BreakRun();
        editor.Type(0, "2");

        editor.Journal.Undo();
        Assert.True(editor.Journal.CanRedo);

        editor.Journal.BreakRun();
        editor.Type(4, "3"); // the end of "1abc"

        Assert.False(editor.Journal.CanRedo);
        Assert.Equal("1abc3", editor.Text);
    }

    [Fact]
    public void ADeepStack_UndoesAndRedoesAllTheWayInBothDirections()
    {
        var editor = new Editor("start");
        var expected = new System.Collections.Generic.List<string> { "start" };

        for (int i = 0; i < 20; i++)
        {
            editor.Journal.BreakRun();
            editor.Type(0, i.ToString());
            expected.Add(editor.Text);
        }

        for (int i = expected.Count - 1; i > 0; i--)
        {
            Assert.Equal(expected[i], editor.Text);
            editor.Journal.Undo();
        }

        Assert.Equal("start", editor.Text);

        for (int i = 1; i < expected.Count; i++)
        {
            editor.Journal.Redo();
            Assert.Equal(expected[i], editor.Text);
        }
    }
}
