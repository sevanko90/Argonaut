using Argonaut.Engine.Text;

namespace Argonaut.Tests.Engine.Text;

public sealed class ControlGlyphsTests
{
    [Theory]
    [InlineData('\u2028', ControlGlyphs.LineSeparatorPicture)]
    [InlineData('\u2029', ControlGlyphs.ParagraphSeparatorPicture)]
    [InlineData('\u0085', ControlGlyphs.NextLinePicture)]
    [InlineData('\r', '\u240D')]
    [InlineData('\0', '\u2400')]
    [InlineData('\u007F', '\u2421')]
    public void DisplayControls_BecomeGlyphs(char control, char glyph)
    {
        Assert.Equal(glyph, ControlGlyphs.ForDisplay(control));
        Assert.True(ControlGlyphs.IsSubstitutionGlyph(glyph));
    }

    [Theory]
    [InlineData('\t')]
    [InlineData('a')]
    [InlineData('\u00E9')]
    [InlineData('\u200B')] // zero-width space: invisible, but not a line break
    public void OtherCharacters_PassThrough(char c) => Assert.Equal(c, ControlGlyphs.ForDisplay(c));

    [Fact]
    public void Text_KeepsItsLengthSoOffsetsStillLineUp()
    {
        const string text = "a\u2028b\u2029c\u0085d\te";
        string shown = ControlGlyphs.ForDisplay(text);

        Assert.Equal(text.Length, shown.Length);
        Assert.Equal("a\u21B5b\u00B6c\u2424d\te", shown);
    }

    [Fact]
    public void CleanText_IsReturnedWithoutCopying()
    {
        string clean = "plain text with \u00FCn\u00EFc\u00F6d\u00E9 and \uD83D\uDE00";
        Assert.Same(clean, ControlGlyphs.ForDisplay(clean));
    }
}
