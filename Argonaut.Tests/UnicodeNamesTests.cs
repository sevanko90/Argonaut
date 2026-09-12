using Argonaut.Infrastructure.Unicode;

namespace Argonaut.Tests;

/// <summary>
/// The embedded Unicode name table. The cases worth pinning are the ones that are not a straight
/// row in UnicodeData.txt: the controls (whose name field is "&lt;control&gt;", so their names come
/// from NameAliases.txt) and the algorithmic ranges (CJK, Hangul, private use), which the file
/// lists only as First/Last markers.
/// </summary>
public class UnicodeNamesTests
{
    [Theory]
    [InlineData(0x0041, "LATIN CAPITAL LETTER A")]
    [InlineData(0x00E9, "LATIN SMALL LETTER E WITH ACUTE")]
    [InlineData(0x2028, "LINE SEPARATOR")]
    [InlineData(0x2029, "PARAGRAPH SEPARATOR")]
    [InlineData(0xFFFD, "REPLACEMENT CHARACTER")]
    [InlineData(0x1F600, "GRINNING FACE")]
    public void ListedCharacters_ReportTheirName(int codePoint, string expected)
    {
        Assert.Equal(expected, UnicodeNames.NameOf(codePoint));
    }

    /// <summary>
    /// Controls have no Name in UnicodeData.txt at all - the field is literally "&lt;control&gt;".
    /// These names come from NameAliases.txt, which is why the generator reads both files.
    /// </summary>
    [Theory]
    [InlineData(0x0000, "NULL")]
    [InlineData(0x000A, "LINE FEED")]
    [InlineData(0x000D, "CARRIAGE RETURN")]
    [InlineData(0x001B, "ESCAPE")]
    [InlineData(0x0085, "NEXT LINE")]
    [InlineData(0x007F, "DELETE")]
    public void Controls_ReportTheirAliasName(int codePoint, string expected)
    {
        Assert.Equal(expected, UnicodeNames.NameOf(codePoint));
    }

    /// <summary>
    /// Ranges listed as First/Last markers, whose names follow a rule rather than being stored.
    /// Hangul is the one that composes rather than appending hex.
    /// </summary>
    [Theory]
    [InlineData(0x4E00, "CJK UNIFIED IDEOGRAPH-4E00")]
    [InlineData(0x9FFF, "CJK UNIFIED IDEOGRAPH-9FFF")]
    [InlineData(0x20000, "CJK UNIFIED IDEOGRAPH-20000")]
    [InlineData(0x17000, "TANGUT IDEOGRAPH-17000")]
    [InlineData(0xE000, "PRIVATE USE-E000")]
    [InlineData(0xAC00, "HANGUL SYLLABLE GA")]
    [InlineData(0xD55C, "HANGUL SYLLABLE HAN")]
    [InlineData(0xD7A3, "HANGUL SYLLABLE HIH")]
    public void AlgorithmicRanges_ComposeTheirName(int codePoint, string expected)
    {
        Assert.Equal(expected, UnicodeNames.NameOf(codePoint));
    }

    [Theory]
    [InlineData(0x0378)] // unassigned
    [InlineData(0xFFFE)] // noncharacter
    public void UnnamedCodePoints_ReportNull(int codePoint)
    {
        Assert.Null(UnicodeNames.NameOf(codePoint));
    }
}
