using Argonaut.Features.Hex;

namespace Argonaut.Tests.Features.Hex;

/// <summary>The hex view's geometry is arithmetic on the offset, and a row's text lines its
/// columns up whatever the row holds.</summary>
public sealed class HexRowsTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16, 1)]
    [InlineData(17, 2)]
    [InlineData(40L * 1024 * 1024 * 1024, 2_684_354_560)]
    public void TheRowCount_CountsAPartialLastRow(long length, long rows)
        => Assert.Equal(rows, HexRows.RowCount(length));

    [Fact]
    public void AnOffset_IsOnRowOffsetOverSixteen()
    {
        Assert.Equal(0, HexRows.RowOf(15));
        Assert.Equal(1, HexRows.RowOf(16));
        Assert.Equal(3_000_000_000L * 16, HexRows.StartOf(3_000_000_000L));
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(0x1_0000_0000, 8)]
    [InlineData(0x1_0000_0001, 9)]
    [InlineData(0x100_0000_0000, 10)]
    public void TheOffsetColumn_IsAtLeastEightDigits_AndGrowsWithTheDocument(long length, int digits)
        => Assert.Equal(digits, HexRows.OffsetDigits(length));

    [Fact]
    public void AFullRow_ShowsOffsetBytesAndCharacters()
    {
        byte[] bytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];
        var line = new char[HexRows.LineLength(8)];

        int length = HexRows.Format(bytes, 0x20, 8, line);

        Assert.Equal("00000020  89 50 4E 47 0D 0A 1A 0A  00 00 00 0D 49 48 44 52  .PNG........IHDR", new string(line, 0, length));
    }

    [Fact]
    public void APartialRow_KeepsItsCharactersUnderTheColumnAbove()
    {
        var line = new char[HexRows.LineLength(8)];

        int length = HexRows.Format("abc"u8, 0x30, 8, line);

        Assert.Equal("00000030  61 62 63" + new string(' ', 42) + "abc", new string(line, 0, length));
        Assert.Equal(HexRows.CharacterColumn(8), new string(line, 0, length).IndexOf("abc", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x00, HexByteClass.Zero)]
    [InlineData(0x20, HexByteClass.Printable)]
    [InlineData(0x7E, HexByteClass.Printable)]
    [InlineData(0x7F, HexByteClass.Other)]
    [InlineData(0x0A, HexByteClass.Other)]
    [InlineData(0xC3, HexByteClass.Other)]
    public void ABytesClass_IsZeroPrintableOrOther(byte value, HexByteClass expected)
        => Assert.Equal(expected, HexRows.ClassOf(value));
}
