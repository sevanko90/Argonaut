using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Raw.Editing;
using Argonaut.Features.Raw.Rows;

namespace Argonaut.Tests;

/// <summary>
/// What the internals inspector reads. The window itself is a Debug-only development tool and is
/// not built outside Debug, but the snapshot behind it is ordinary code that can go quietly wrong
/// - and a debugging aid that lies is worse than not having one, because it is trusted at exactly
/// the moment something else is already confusing.
///
/// These also serve as executable statements of the invariants the window is laid out to make
/// checkable by eye: spans are ordered and disjoint, and the last span's own delta plus
/// everything before it is the whole document's delta.
/// </summary>
public class RawEditSnapshotTests
{
    private static RawEditController Editing(string content, int wrapWidth = 80)
    {
        var source = new MemoryByteSource(Encoding.UTF8.GetBytes(content));
        var scan = RawSegmentIndex.StartIndexing(source, wrapWidth);
        scan.IndexingTask.GetAwaiter().GetResult();

        return new RawEditController(scan, source);
    }

    private static string LongText(int lines)
    {
        var text = new StringBuilder();
        for (int i = 0; i < lines; i++)
            text.Append($"line {i}\n");

        return text.ToString();
    }

    [Fact]
    public void BeforeAnyEdit_ItDescribesTheFileItself()
    {
        var state = Editing("alpha\nbeta\n").Describe();

        Assert.True(state.IsUnedited);
        Assert.Equal(state.OriginalLength, state.DocumentLength);
        Assert.Equal(0, state.ByteDelta);
        Assert.Equal(1, state.PieceCount);
        Assert.Equal(0, state.SpanCount);
        Assert.Equal(0, state.ScratchBytesUsed);
        Assert.Equal(0, state.TotalDerivedRows);
        Assert.False(state.NeedsRebuild);
    }

    [Fact]
    public void AfterTyping_ScratchHoldsExactlyWhatWasTyped()
    {
        var editor = Editing("alpha\nbeta\n");
        editor.Caret.PlaceAt(3);
        editor.Type("XY");

        var state = editor.Describe();

        Assert.False(state.IsUnedited);
        Assert.Equal(2, state.ByteDelta);
        Assert.Equal(2, state.ScratchBytesUsed);
        Assert.Equal(1, state.ScratchChunkCount);

        // Split, typed, remainder - the piece table's whole trick in three entries.
        Assert.Equal(3, state.PieceCount);
        Assert.Equal(3, state.Pieces.Count);
        Assert.True(state.Pieces[0].FromOriginal);
        Assert.False(state.Pieces[1].FromOriginal);
        Assert.Equal(2, state.Pieces[1].Length);
        Assert.True(state.Pieces[2].FromOriginal);

        // Logical starts must tile the document with no gap and no overlap, which is what makes
        // the inspector's bottom bar a picture of the document rather than of the list.
        long expected = 0;
        foreach (var piece in state.Pieces)
        {
            Assert.Equal(expected, piece.LogicalStart);
            expected += piece.Length;
        }

        Assert.Equal(state.DocumentLength, expected);
    }

    [Fact]
    public void AfterTyping_OneSpanBracketsTheEdit()
    {
        var editor = Editing("alpha\nbeta\ngamma\n");
        editor.Caret.PlaceAt(8);
        editor.Type("Z");

        var state = editor.Describe();

        Assert.Equal(1, state.SpanCount);
        var span = Assert.Single(state.Spans);
        Assert.True(span.StartOffset <= 8, $"span starts at {span.StartOffset}, after the edit");
        Assert.True(span.EndOffset >= 9, $"span ends at {span.EndOffset}, before the edit");
        Assert.True(span.RowsHeld > 0);
        Assert.Equal(1, span.ByteDelta);
        Assert.Equal(0, span.ByteDeltaBefore);
        Assert.True(state.BudgetUsed > 0 && state.BudgetUsed < 1);
    }

    [Fact]
    public void WithSeveralSpans_TheyAreOrderedAndDisjointAndTheirDeltasAddUp()
    {
        var editor = Editing(LongText(20_000));

        foreach (long at in new[] { 100L, 60_000L, 130_000L })
        {
            editor.Caret.PlaceAt(at);
            Assert.Equal(RawEditOutcome.Applied, editor.Type("zz"));
        }

        var state = editor.Describe();
        Assert.Equal(3, state.SpanCount);

        long previousEnd = 0;
        int previousRowEnd = 0;
        foreach (var span in state.Spans)
        {
            Assert.True(span.StartOffset >= previousEnd,
                $"span {span.Index} starts at {span.StartOffset}, inside the one ending at {previousEnd}");
            Assert.True(span.StartRow >= previousRowEnd,
                $"span {span.Index} starts at row {span.StartRow}, inside the one ending at {previousRowEnd}");

            previousEnd = span.EndOffset;
            previousRowEnd = span.StartRow + span.RowsHeld;
        }

        // The running totals are what displace every untouched row after the last span, so the
        // two halves of the last row of the inspector's span table must come to the document's
        // own delta. If they ever do not, the rows past the last span are being drawn in the
        // wrong place.
        var last = state.Spans[^1];
        Assert.Equal(state.ByteDelta, last.ByteDeltaBefore + last.ByteDelta);
        Assert.Equal(state.RowDelta, last.RowDeltaBefore + last.RowDelta);
    }

    [Fact]
    public void TypingARun_CostsOnePieceRatherThanOnePerKeystroke()
    {
        var editor = Editing(LongText(200));
        editor.Caret.PlaceAt(100);

        for (int i = 0; i < 400; i++)
            editor.Type("x");

        var state = editor.Describe();

        // Split, the whole typed run, remainder. Without coalescing this was 400 pieces of one
        // byte each - which is not merely untidy: GetContiguousSpan truncates at every piece
        // boundary, so a row scan across the run would return a single byte per call, each one
        // paying a fresh binary search to be found.
        Assert.Equal(3, state.PieceCount);
        Assert.Equal(400, state.ScratchBytesUsed);
        Assert.Equal(400, state.Pieces[1].Length);
        Assert.False(state.Pieces[1].FromOriginal);
    }

    [Fact]
    public void TypingAfterAnUndo_DoesNotReuseTheRunItRewound()
    {
        // Scratch is append-only and an undo rewinds only the piece list, so the run's piece can
        // outlive being the tail of scratch. Extending it then would hand the redone bytes to the
        // wrong place.
        var editor = Editing("alpha\nbeta\n");
        editor.Caret.PlaceAt(5);
        editor.Type("1");
        editor.Type("2");

        editor.Undo();
        editor.Type("9");

        var bytes = new byte[editor.Document.AvailableLength];
        editor.Document.CopyTo(0, bytes);
        Assert.Equal("alpha9\nbeta\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TypingAwayFromTheRun_StartsANewPiece()
    {
        var editor = Editing(LongText(200));

        editor.Caret.PlaceAt(100);
        editor.Type("aa");

        editor.Caret.PlaceAt(500);
        editor.Type("bb");

        var state = editor.Describe();
        Assert.Equal(5, state.PieceCount);
        Assert.Equal(4, state.ScratchBytesUsed);
    }

    [Fact]
    public void ThePieceListIsCapped_AndSaysHowMuchItLeftOut()
    {
        var editor = Editing(LongText(2_000));

        // Deliberately not one run: each edit is far enough from the last that it cannot extend
        // the previous piece, which is what makes the list long enough to be capped.
        for (int i = 0; i < 20; i++)
        {
            editor.Caret.PlaceAt(100 + (i * 200));
            editor.Type("q");
        }

        var state = editor.Describe(maxPieces: 5);

        Assert.Equal(5, state.Pieces.Count);
        Assert.Equal(state.PieceCount - 5, state.PiecesOmitted);
        Assert.True(state.PiecesOmitted > 0, "the cap was never reached, so nothing was tested");
    }

    [Fact]
    public void UndoIsReportedAsTheJournalSeesIt()
    {
        var editor = Editing("alpha\n");
        editor.Caret.PlaceAt(5);
        editor.Type("!");

        var typed = editor.Describe();
        Assert.Equal(1, typed.UndoDepth);
        Assert.True(typed.CanUndo);
        Assert.False(typed.CanRedo);

        editor.Undo();

        var undone = editor.Describe();
        Assert.False(undone.CanUndo);
        Assert.True(undone.CanRedo);
        Assert.Equal(0, undone.ByteDelta);
    }

    [Fact]
    public void TheCaretAndSelectionAreReported()
    {
        var editor = Editing("alpha\nbeta\n");
        editor.Caret.PlaceAt(2);
        editor.Caret.ExtendTo(7);

        var state = editor.Describe();

        Assert.Equal(7, state.CaretOffset);
        Assert.Equal(2, state.SelectionStart);
        Assert.Equal(7, state.SelectionEnd);
    }
}
