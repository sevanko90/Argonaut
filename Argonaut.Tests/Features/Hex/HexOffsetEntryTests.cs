using Argonaut.Features.Hex;

namespace Argonaut.Tests.Features.Hex;

public sealed class HexOffsetEntryTests
{
    [Theory]
    [InlineData("8000", 8000)]
    [InlineData("0x1F40", 0x1F40)]
    [InlineData("0X1f40", 0x1F40)]
    [InlineData("1F40h", 0x1F40)]
    [InlineData("1f40", 0x1F40)]          // a hex letter makes it hex
    [InlineData("1000", 1000)]            // digits alone are decimal
    [InlineData("  8,000  ", 8000)]
    [InlineData("0x1F_40", 0x1F40)]
    [InlineData("0", 0)]
    [InlineData("+16", 116)]
    [InlineData("-0x10", 84)]
    [InlineData("+ 0x10", 116)]
    public void Reads(string text, long expected)
    {
        Assert.True(HexOffsetEntry.TryParse(text, caret: 100, out long offset));
        Assert.Equal(expected, offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x")]
    [InlineData("h")]
    [InlineData("+")]
    [InlineData("12g")]
    [InlineData("-200")]                  // before the start
    [InlineData("0xFFFFFFFFFFFFFFFF")]    // past what an offset can be
    [InlineData("99999999999999999999")]
    [InlineData("1.5")]
    [InlineData(null)]
    public void Refuses(string? text)
    {
        Assert.False(HexOffsetEntry.TryParse(text, caret: 100, out _));
    }

    [Fact]
    public void ARelativeStepPastWhatAnOffsetCanBe_IsRefused()
    {
        Assert.False(HexOffsetEntry.TryParse("+0x7FFFFFFFFFFFFFFF", caret: 100, out _));
    }
}
