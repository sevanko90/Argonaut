using System.Text;
using Argonaut.Features.Hex;

namespace Argonaut.Tests.Features.Hex;

/// <summary>A row read as UTF-8: each character at its first byte's cell, its other bytes empty
/// within the row and marked on the next, three bytes looked at either side at most.</summary>
public sealed class HexUtf8Tests
{
    private static HexCell[] Read(byte[] window, int before, int count)
    {
        var cells = new HexCell[count];
        HexUtf8.ReadRow(window, before, count, cells);
        return cells;
    }

    [Fact]
    public void ACharacter_DrawsAtItsFirstByte_AndItsOtherBytesAreEmpty()
    {
        var cells = Read([(byte)'a', 0xE2, 0x82, 0xAC, (byte)'b'], 0, 5);

        Assert.Equal(new HexCell(HexCellKind.Character, new Rune('a'), 1), cells[0]);
        Assert.Equal(new HexCell(HexCellKind.Character, new Rune('€'), 3), cells[1]);
        Assert.Equal(HexCellKind.Continuation, cells[2].Kind);
        Assert.Equal(HexCellKind.Continuation, cells[3].Kind);
        Assert.Equal(new Rune('b'), cells[4].Character);
    }

    [Fact]
    public void ACharacterCrossingTheRowsEnd_IsReadWhole_FromTheBytesAfterIt()
    {
        // The row is "x" and the euro's first byte; its other two are past the row.
        var cells = Read([(byte)'x', 0xE2, 0x82, 0xAC], 0, 2);

        Assert.Equal(new HexCell(HexCellKind.Character, new Rune('€'), 3), cells[1]);
    }

    [Fact]
    public void BytesOfACharacterBegunOnTheRowAbove_AreMarkedCarriedOver()
    {
        // Two bytes before the row: 'q' and the euro's first byte. The row starts with its other two.
        var cells = Read([(byte)'q', 0xE2, 0x82, 0xAC, (byte)'z'], 2, 3);

        Assert.Equal(HexCellKind.CarriedOver, cells[0].Kind);
        Assert.Equal(HexCellKind.CarriedOver, cells[1].Kind);
        Assert.Equal(new Rune('z'), cells[2].Character);
    }

    [Fact]
    public void ALoneContinuationByte_InvalidBytes_AndControls_AreUnreadable()
    {
        var cells = Read([0x82, 0xFF, 0x0A, 0xC3], 0, 4);

        Assert.All(cells, c => Assert.Equal(HexCellKind.Unreadable, c.Kind));
    }

    [Fact]
    public void TheCharacterAByteBelongsTo_IsFoundFromItsFirstByte()
    {
        byte[] window = [(byte)'a', 0xF0, 0x9F, 0x98, 0x80];

        Assert.Equal(new Rune('a'), HexUtf8.CharacterAt(window, 0));
        Assert.Equal(new Rune(0x1F600), HexUtf8.CharacterAt(window, 1));
        Assert.Equal(new Rune(0x1F600), HexUtf8.CharacterAt(window, 4));
        Assert.Null(HexUtf8.CharacterAt([0x98, 0x80], 1));
    }

    [Fact]
    public void AsText_ReadsCharacters_AndDotsTheRest()
    {
        Assert.Equal("a€.b.", HexUtf8.AsText([(byte)'a', 0xE2, 0x82, 0xAC, 0x0A, (byte)'b', 0xE2]));
    }
}
