using System.Text;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Indexing;

namespace Argonaut.Tests.Features.Json.Hints;

public class ColourHintTests
{
    private static uint? Classify(string text)
        => ColourHintClassifier.TryClassify(Encoding.UTF8.GetBytes(text), out uint argb, out _) ? argb : null;

    [Theory]
    [InlineData("#1E90FF", 0xFF1E90FFu)]
    [InlineData("#1e90ff", 0xFF1E90FFu)]
    [InlineData("#2E8B57CC", 0xCC2E8B57u)]
    [InlineData("rgb(255, 99, 71)", 0xFFFF6347u)]
    [InlineData("rgb(255 99 71)", 0xFFFF6347u)]
    [InlineData("rgba(255, 99, 71, 0.5)", 0x80FF6347u)]
    [InlineData("rgb(255 99 71 / 50%)", 0x80FF6347u)]
    [InlineData("rgb(100%, 0%, 0%)", 0xFFFF0000u)]
    [InlineData("hsl(0, 100%, 50%)", 0xFFFF0000u)]
    [InlineData("hsl(120deg 100% 25%)", 0xFF008000u)]
    [InlineData("hsla(240, 100%, 50%, 0.25)", 0x400000FFu)]
    [InlineData("hsl(210, 60%, 45%)", 0xFF2E73B8u)]
    public void ACssColour_IsRead(string text, uint expected) => Assert.Equal(expected, Classify(text));

    [Theory]
    [InlineData("#123")]
    [InlineData("#f808")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    [InlineData("#GGGGGG")]
    [InlineData("#")]
    [InlineData("rgb(256, 0, 0)")]
    [InlineData("rgb(1, 2)")]
    [InlineData("rgb(1, 2, 3, 4, 5)")]
    [InlineData("rgb(1,2,3")]
    [InlineData("rgb(1, 2, 3, 2)")]
    [InlineData("hsl(0, 100, 50)")]
    [InlineData("hsl(0, 120%, 50%)")]
    [InlineData("rgb(1px, 2, 3)")]
    [InlineData("red")]
    [InlineData("1E90FF")]
    public void AnythingElse_IsNotAColour(string text) => Assert.Null(Classify(text));

    private static (string? Text, Avalonia.Media.Color? Swatch) Hint(string value)
    {
        var provider = new ColourHintProvider();
        byte[] raw = Encoding.UTF8.GetBytes(value);
        if (!provider.TryClassify(JsonTokenKind.String, raw, raw.Length, out var candidate))
            return (null, null);
        var hint = provider.FormatHint(candidate, raw, raw.Length, 0);
        return (hint?.Text, hint?.Swatch);
    }

    [Fact]
    public void TheChip_SaysTheColourInTheOtherNotation_BesideItsSwatch()
    {
        Assert.Equal(("rgb(30, 144, 255)", Avalonia.Media.Color.FromUInt32(0xFF1E90FF)), Hint("#1E90FF"));
        Assert.Equal("rgba(46, 139, 87, 0.8)", Hint("#2E8B57CC").Text);
        Assert.Equal("#FF6347", Hint("rgb(255, 99, 71)").Text);
        Assert.Equal("#FF634780", Hint("rgba(255, 99, 71, 0.5)").Text);
    }

    [Fact]
    public void OnlyStringsAreRead()
        => Assert.False(new ColourHintProvider().TryClassify(JsonTokenKind.Number, "#fff"u8, 4, out _));
}
