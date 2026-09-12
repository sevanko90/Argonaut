using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Everything about the caret that can be expressed in byte offsets, tested with no UI: the
/// controller deliberately leaves only the two genuinely pixel-shaped operations (vertical
/// movement and click-to-place) to the surface, so nearly all the behaviour worth testing is
/// reachable here.
/// </summary>
public class RawCaretControllerTests
{
    private static (RawCaretController Caret, RawSegmentIndex Index, IByteSource Source) Over(
        string text, int wrapWidth = 80)
    {
        var source = new ArrayByteSource(Encoding.UTF8.GetBytes(text));
        var index = RawSegmentIndex.StartIndexing(source, wrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();
        return (new RawCaretController(index, source), index, source);
    }

    [Fact]
    public void StartsAtTheBeginningWithNothingSelected()
    {
        var (caret, _, _) = Over("hello");

        Assert.Equal(0, caret.Caret.Offset);
        Assert.True(caret.Selection.IsEmpty);
    }

    [Fact]
    public void PlaceAt_SnapsAMidCharacterOffsetOntoACharacter()
    {
        // Byte 2 is the second byte of the two-byte 'é'.
        var (caret, _, _) = Over("aéb");

        caret.PlaceAt(2);

        Assert.Equal(1, caret.Caret.Offset);
        Assert.True(caret.Selection.IsEmpty);
    }

    [Fact]
    public void MoveRight_StepsOverAWholeCharacter()
    {
        var (caret, _, _) = Over("aéb");

        caret.MoveRight(extend: false);
        Assert.Equal(1, caret.Caret.Offset);

        caret.MoveRight(extend: false);
        Assert.Equal(3, caret.Caret.Offset); // skipped the second byte of 'é'
    }

    [Fact]
    public void MoveLeft_AtTheStartStaysPut()
    {
        var (caret, _, _) = Over("abc");

        caret.MoveLeft(extend: false);

        Assert.Equal(0, caret.Caret.Offset);
    }

    [Fact]
    public void MoveRight_AtTheEndStaysPut()
    {
        var (caret, _, source) = Over("abc");
        caret.MoveToDocumentEnd(extend: false);

        caret.MoveRight(extend: false);

        Assert.Equal(source.AvailableLength, caret.Caret.Offset);
    }

    [Fact]
    public void MovingWithoutExtending_CollapsesASelectionToItsNearEdge()
    {
        var (caret, _, _) = Over("abcdef");
        caret.PlaceAt(1);
        caret.ExtendTo(4);
        Assert.Equal(3, caret.Selection.Length);

        caret.MoveLeft(extend: false);
        Assert.Equal(1, caret.Caret.Offset);   // the selection's start, not one left of its end
        Assert.True(caret.Selection.IsEmpty);

        caret.PlaceAt(1);
        caret.ExtendTo(4);
        caret.MoveRight(extend: false);
        Assert.Equal(4, caret.Caret.Offset);   // the selection's end
        Assert.True(caret.Selection.IsEmpty);
    }

    [Fact]
    public void ExtendingReversesDirectionCorrectly()
    {
        var (caret, _, _) = Over("abcdef");
        caret.PlaceAt(3);

        caret.ExtendTo(5);
        Assert.Equal(3, caret.Selection.Start);
        Assert.Equal(5, caret.Selection.End);

        // Dragging back past the anchor flips the selection rather than emptying it.
        caret.ExtendTo(1);
        Assert.Equal(1, caret.Selection.Start);
        Assert.Equal(3, caret.Selection.End);
        Assert.Equal(1, caret.Caret.Offset);
    }

    [Fact]
    public void HomeAndEnd_BoundTheRowsDrawnText()
    {
        // "ab\ncd": row 0 covers bytes 0..3 but only draws "ab".
        var (caret, _, _) = Over("ab\ncd");
        caret.PlaceAt(1);

        caret.MoveToRowEnd(extend: false);
        Assert.Equal(2, caret.Caret.Offset); // before the newline, never inside it

        caret.MoveToRowStart(extend: false);
        Assert.Equal(0, caret.Caret.Offset);
    }

    [Fact]
    public void EndOnASoftWrappedRow_StaysOnThatRow()
    {
        // At a cap of 8 the row boundary at byte 8 is both the end of row 0 and the start of
        // row 1. End must leave the caret drawn on row 0, which is what the affinity says.
        var (caret, _, _) = Over(new string('x', 20), wrapWidth: 8);
        caret.PlaceAt(3);

        caret.MoveToRowEnd(extend: false);

        Assert.Equal(8, caret.Caret.Offset);
        Assert.Equal(CaretAffinity.Upstream, caret.Caret.Affinity);
    }

    [Fact]
    public void HomeOnTheFollowingRow_IsTheSameOffsetWithTheOppositeAffinity()
    {
        var (caret, _, _) = Over(new string('x', 20), wrapWidth: 8);
        caret.PlaceAt(10);

        caret.MoveToRowStart(extend: false);

        Assert.Equal(8, caret.Caret.Offset);
        Assert.Equal(CaretAffinity.Downstream, caret.Caret.Affinity);
    }

    [Fact]
    public void MovingLeftAcrossAWrapBoundary_LandsOnTheRowAbove()
    {
        var (caret, _, _) = Over(new string('x', 20), wrapWidth: 8);
        caret.PlaceAt(9);

        caret.MoveLeft(extend: false);

        Assert.Equal(8, caret.Caret.Offset);
        Assert.Equal(CaretAffinity.Upstream, caret.Caret.Affinity);
    }

    [Fact]
    public void ACaretNeverLandsInsideALineEnding()
    {
        var (caret, _, source) = Over("ab\r\ncd");

        var visited = new List<long>();
        long previous = -1;
        while (caret.Caret.Offset != previous)
        {
            visited.Add(caret.Caret.Offset);
            previous = caret.Caret.Offset;
            caret.MoveRight(extend: false);
        }

        Assert.DoesNotContain(3L, visited); // between '\r' and '\n'
        Assert.Equal(source.AvailableLength, visited[^1]);
    }

    [Fact]
    public void SelectAll_CoversTheDocument()
    {
        var (caret, _, source) = Over("hello world");

        caret.SelectAll();

        Assert.Equal(0, caret.Selection.Start);
        Assert.Equal(source.AvailableLength, caret.Selection.End);
        Assert.Equal(source.AvailableLength, caret.Caret.Offset);
    }

    [Fact]
    public void ClearSelection_LeavesTheCaretWhereItIs()
    {
        var (caret, _, _) = Over("abcdef");
        caret.PlaceAt(2);
        caret.ExtendTo(5);

        caret.ClearSelection();

        Assert.Equal(5, caret.Caret.Offset);
        Assert.True(caret.Selection.IsEmpty);
    }

    [Fact]
    public void Moved_FiresOnlyWhenSomethingActuallyChanged()
    {
        var (caret, _, _) = Over("abc");
        int moves = 0;
        caret.Moved += (_, _) => moves++;

        caret.PlaceAt(1);
        Assert.Equal(1, moves);

        caret.PlaceAt(1); // same place, same empty selection
        Assert.Equal(1, moves);

        caret.MoveLeft(extend: false);
        Assert.Equal(2, moves);
    }

    [Fact]
    public void OnAnEmptyDocument_EveryMovementIsANoOp()
    {
        var (caret, _, _) = Over(string.Empty);

        caret.MoveRight(extend: false);
        caret.MoveLeft(extend: false);
        caret.MoveToRowEnd(extend: false);
        caret.MoveToDocumentEnd(extend: false);

        Assert.Equal(0, caret.Caret.Offset);
        Assert.True(caret.Selection.IsEmpty);
    }

    [Fact]
    public void Intersect_GivesARowItsSliceOfAMultiRowSelection()
    {
        var selection = new RawSelection(5, 25);

        Assert.Null(selection.Intersect(0, 5));
        Assert.Equal((5L, 10L), selection.Intersect(0, 10));
        Assert.Equal((10L, 20L), selection.Intersect(10, 20));
        Assert.Equal((20L, 25L), selection.Intersect(20, 30));
        Assert.Null(selection.Intersect(25, 40));
    }
}
