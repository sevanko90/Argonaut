using Argonaut.Engine.Bytes;
using Argonaut.Features.Hex;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Hex;

/// <summary>The caret and selection as two boundaries: every key's move, kept inside the document,
/// on a byte with nothing selected and up to the end with a selection.</summary>
public sealed class HexCursorTests
{
    private static HexCursor Over(long length) => new(() => length);

    private static HexCursor At(long offset, long length = 1000)
    {
        var cursor = Over(length);
        cursor.Place(offset);
        return cursor;
    }

    [Theory]
    [InlineData(HexMove.Left, 99)]
    [InlineData(HexMove.Right, 101)]
    [InlineData(HexMove.Up, 84)]
    [InlineData(HexMove.Down, 116)]
    [InlineData(HexMove.PageUp, 20)]
    [InlineData(HexMove.PageDown, 180)]
    [InlineData(HexMove.RowStart, 96)]
    [InlineData(HexMove.RowEnd, 111)]
    [InlineData(HexMove.DocumentStart, 0)]
    [InlineData(HexMove.DocumentEnd, 999)]
    public void EachMove_LandsOnAByte(HexMove move, long expected)
    {
        var cursor = At(100);
        cursor.Move(move, extend: false, pageRows: 5);
        Assert.Equal(HexCaret.At(expected), cursor.Caret);
    }

    [Fact]
    public void ACaret_StartsOnTheFirstByte_OnceThereIsOne()
    {
        long length = 0;
        var cursor = new HexCursor(() => length);
        Assert.Null(cursor.Caret);

        cursor.Place(5);
        Assert.Null(cursor.Caret);

        length = 10;
        Assert.Equal(HexCaret.At(0), cursor.Caret);
    }

    [Fact]
    public void StepsPastTheEnds_StayOnTheirRow_ButPagesGoAsFarAsTheyCan()
    {
        var cursor = At(5, length: 40);
        cursor.Move(HexMove.Up, false, 5);
        Assert.Equal(HexCaret.At(5), cursor.Caret);
        cursor.Move(HexMove.PageUp, false, 5);
        Assert.Equal(HexCaret.At(0), cursor.Caret);

        cursor.Place(35);
        cursor.Move(HexMove.Down, false, 5);
        Assert.Equal(HexCaret.At(35), cursor.Caret);
        cursor.Move(HexMove.PageDown, false, 5);
        Assert.Equal(HexCaret.At(39), cursor.Caret);
        cursor.Move(HexMove.Right, false, 5);
        Assert.Equal(HexCaret.At(39), cursor.Caret);
    }

    [Fact]
    public void Extending_KeepsTheAnchor_AndReachesTheEndOfTheDocument()
    {
        var cursor = At(30, length: 40);
        cursor.Move(HexMove.Right, extend: true, pageRows: 5);
        Assert.Equal(new HexCaret(30, 31), cursor.Caret);
        Assert.Equal(30, cursor.Caret!.Value.Byte);

        cursor.Move(HexMove.DocumentEnd, extend: true, pageRows: 5);
        Assert.Equal(new HexCaret(30, 40), cursor.Caret);
        Assert.Equal(39, cursor.Caret!.Value.Byte);

        cursor.Move(HexMove.DocumentStart, extend: true, pageRows: 5);
        Assert.Equal(new HexCaret(30, 0), cursor.Caret);
        Assert.Equal(0, cursor.Caret!.Value.Byte);
    }

    [Fact]
    public void ExtendingToTheRowEnd_TakesItsLastByte()
    {
        var cursor = At(20);
        cursor.Move(HexMove.RowEnd, extend: true, pageRows: 5);
        Assert.Equal(new HexCaret(20, 32), cursor.Caret);
    }

    [Fact]
    public void AMoveWithoutShift_LetsTheSelectionGo_FromTheCaretsByte()
    {
        var cursor = At(20);
        cursor.Select(new HexCaret(20, 30));
        cursor.Move(HexMove.Right, extend: false, pageRows: 5);
        Assert.Equal(HexCaret.At(30), cursor.Caret);
    }

    [Fact]
    public void ExtendTo_IncludesTheByteClicked_EitherSideOfTheAnchor()
    {
        var cursor = At(20);
        cursor.ExtendTo(25);
        Assert.Equal(new HexCaret(20, 26), cursor.Caret);
        cursor.ExtendTo(10);
        Assert.Equal(new HexCaret(20, 10), cursor.Caret);
    }

    [Fact]
    public void Spanning_SelectsBothBytes_WhicheverWayRound()
    {
        Assert.Equal((4L, 9L), (HexCaret.Spanning(4, 8).Start, HexCaret.Spanning(4, 8).End));
        Assert.Equal((4L, 9L), (HexCaret.Spanning(8, 4).Start, HexCaret.Spanning(8, 4).End));
        Assert.Equal(1, HexCaret.Spanning(6, 6).Length);
    }

    [Fact]
    public void SelectAll_CoversTheDocument_AndMovesAreReported()
    {
        var cursor = Over(100);
        int moved = 0;
        cursor.Moved += (_, _) => moved++;

        cursor.SelectAll();
        cursor.SelectAll();
        Assert.Equal(new HexCaret(0, 100), cursor.Caret);
        Assert.Equal(1, moved);
    }

    [Fact]
    public void Position_IsTheCaret_WhileItsRowIsOnScreen_ElseTheTopRow()
    {
        var cursor = At(100);
        Assert.Equal(ByteRange.At(100), cursor.Position);

        cursor.ViewportRows = (2, 20);
        Assert.Equal(ByteRange.At(100), cursor.Position);

        cursor.ViewportRows = (30, 50);
        Assert.Equal(ByteRange.At(30 * 16), cursor.Position);

        cursor.ViewportRows = (0, 20);
        cursor.Select(new HexCaret(100, 104));
        Assert.Equal(new ByteRange(100, 4), cursor.Position);
    }

    [Fact]
    public void ARangePastTheEnd_IsKeptInside()
    {
        var cursor = Over(50);
        cursor.Reveal(new ByteRange(40, 100));
        Assert.Equal(new HexCaret(50, 40), cursor.Caret);

        cursor.Reveal(ByteRange.At(80));
        Assert.Equal(HexCaret.At(49), cursor.Caret);
        Assert.True(cursor.TakeReveal());
        Assert.False(cursor.TakeReveal());
    }

    [Fact]
    public void AGrowingDocument_LetsTheCaretFollowWhatArrives()
    {
        var source = new GrowingByteSource(new byte[100], initiallyAvailable: 20);
        var cursor = new HexCursor(() => source.AvailableLength);

        cursor.Move(HexMove.DocumentEnd, false, 5);
        Assert.Equal(HexCaret.At(19), cursor.Caret);

        source.Reveal(60);
        cursor.Move(HexMove.DocumentEnd, false, 5);
        Assert.Equal(HexCaret.At(79), cursor.Caret);
    }
}
