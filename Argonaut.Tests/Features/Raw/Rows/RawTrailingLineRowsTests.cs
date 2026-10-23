using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Editing;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests.Features.Raw.Rows;

public class RawTrailingLineRowsTests
{
    private static (RawTrailingLineRows Rows, RawSegmentIndex Index) Over(string text)
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes(text));
        var index = RawSegmentIndex.StartIndexing(source, 80);
        index.IndexingTask.GetAwaiter().GetResult();
        return (new RawTrailingLineRows(index, source), index);
    }

    [Fact]
    public void AnEmptyDocument_HasLineOne()
    {
        var (rows, index) = Over("");

        Assert.Equal(0, index.RowCount);
        Assert.Equal(1, rows.RowCount);
        Assert.Equal(new RawRowInfo(0, 0, false, 1), rows.GetRowInfo(0));
        Assert.Equal(0, rows.RowForOffset(0));
        Assert.Equal(1, rows.LineContaining(0));
    }

    [Fact]
    public void AFinalNewline_IsFollowedByAnEmptyLine()
    {
        var (rows, _) = Over("ab\ncd\n");

        Assert.Equal(3, rows.RowCount);
        Assert.Equal(new RawRowInfo(6, 6, false, 3), rows.GetRowInfo(2));
        Assert.Equal(2, rows.RowForOffset(6));
        Assert.Equal(3, rows.LineContaining(2));
    }

    [Fact]
    public void ALastLineWithNoNewline_GetsNoExtraLine()
    {
        var (rows, index) = Over("ab\ncd");

        Assert.Equal(index.RowCount, rows.RowCount);
        Assert.Null(rows.RowForOffset(5));
    }

    [Fact]
    public async Task TheLineFollowsEditsToTheLastByte()
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes("ab"));
        var index = RawSegmentIndex.StartIndexing(source, 80);
        await index.IndexingTask;
        var editor = new RawEditController(index, source);
        var rows = new RawTrailingLineRows(editor.RowIndex, editor.Document);
        Assert.Equal(1, rows.RowCount);

        editor.Caret.PlaceAt(2);
        editor.Insert("\n"u8);

        Assert.Equal(2, rows.RowCount);
        Assert.Equal(2, rows.GetRowInfo(1).LineNumber);
    }

    [Fact]
    public async Task ACaretAfterAFinalNewline_IsOnTheEmptyLine()
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes("ab\n"));
        var index = RawSegmentIndex.StartIndexing(source, 80);
        await index.IndexingTask;
        var caret = new RawCaretController(index, source);

        caret.MoveToDocumentEnd(extend: false);
        caret.MoveToRowStart(extend: false);

        // The start of the line it is on, not of "ab" above it.
        Assert.Equal(3, caret.Caret.Offset);
    }
}
