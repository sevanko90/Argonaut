using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// What the raw view's status gutter reports. The interesting cases are the ones where bytes and
/// characters disagree - multi-byte text, invalid bytes, a column counted across a soft wrap -
/// and the caps, which exist because a "line" here can be a multi-GB minified document.
/// </summary>
public class RawCaretReadoutTests
{
    private static RawCaretReadout Describe(byte[] content, long caretOffset,
        RawSelection selection = default, int wrapWidth = 80)
    {
        var source = new ArrayByteSource(content);
        var index = RawSegmentIndex.StartIndexing(source, wrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();

        return RawCaretReadout.Describe(index, source, new RawCaret(caretOffset), selection);
    }

    private static RawCaretReadout Describe(string content, long caretOffset,
        RawSelection selection = default, int wrapWidth = 80)
        => Describe(Encoding.UTF8.GetBytes(content), caretOffset, selection, wrapWidth);

    [Theory]
    [InlineData("abc", 0, "U+0061 LATIN SMALL LETTER A")]
    [InlineData("a\u2028b", 1, "U+2028 LINE SEPARATOR")]
    [InlineData("a\u0085b", 1, "U+0085 NEXT LINE")]
    [InlineData("a\nb", 1, "U+000A LINE FEED")]
    [InlineData("aéb", 1, "U+00E9 LATIN SMALL LETTER E WITH ACUTE")]
    [InlineData("a\U0001F680b", 1, "U+1F680 ROCKET")]
    public void Character_IsNamedFromTheRealBytes(string content, long offset, string expected)
    {
        Assert.Equal(expected, Describe(content, offset).Character);
    }

    /// <summary>
    /// The display substitutions must not leak into this: the row draws U+2028 as a glyph, but the
    /// gutter exists to say what the file actually holds.
    /// </summary>
    [Fact]
    public void InvalidBytes_AreReportedAsBytes()
    {
        Assert.Equal("0xFF invalid UTF-8", Describe([(byte)'a', 0xFF, (byte)'b'], 1).Character);
    }

    [Fact]
    public void CaretAtEndOfFile_HasNoCharacter()
    {
        Assert.Equal(RawCaretReadout.EndOfFile, Describe("abc", 3).Character);
    }

    [Fact]
    public void ByteOffset_IsTheCaretOffset()
    {
        Assert.Equal(5, Describe("abc\ndef", 5).ByteOffset);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(2, 1, 3)]
    [InlineData(4, 2, 1)]
    [InlineData(5, 2, 2)]
    public void LineAndColumn_CountFromTheStartOfTheLine(long offset, int line, int column)
    {
        var readout = Describe("abc\ndef\nghi", offset);

        Assert.Equal(line, readout.LineNumber);
        Assert.Equal(column, readout.Column);
    }

    /// <summary>A column is a character count, so multi-byte characters advance it by one.</summary>
    [Fact]
    public void Column_CountsCharactersNotBytes()
    {
        // "héllo": h is 1 byte, é is 2, so the caret on the second l is at byte 4, column 4.
        var readout = Describe("héllo", 4);

        Assert.Equal(1, readout.LineNumber);
        Assert.Equal(4, readout.Column);
    }

    /// <summary>
    /// A soft wrap is the viewer's doing, not the file's, so the column keeps counting across it -
    /// which means walking back through the continuation rows to find where the line started.
    /// </summary>
    [Fact]
    public void Column_CountsAcrossASoftWrap()
    {
        string line = new('x', 50);
        var readout = Describe(line, 45, wrapWidth: 20);

        Assert.Equal(1, readout.LineNumber);
        Assert.Equal(46, readout.Column);
    }

    /// <summary>
    /// Past the cap the answer is "not from here" rather than a walk back through gigabytes. Both
    /// halves go: without reaching the line start there is no line number to report either.
    /// </summary>
    [Fact]
    public void LineAndColumn_AreRefusedPastTheScanCap()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('x', RawCaretReadout.ColumnScanBytes + 5_000));
        var readout = Describe(content, content.Length - 1);

        Assert.Null(readout.LineNumber);
        Assert.Null(readout.Column);
        Assert.Equal(content.Length - 1, readout.ByteOffset);
    }

    [Fact]
    public void NoSelection_ReportsNothingSelected()
    {
        var readout = Describe("abc\ndef", 1);

        Assert.Equal(0, readout.SelectionBytes);
        Assert.Null(readout.SelectionCharacters);
    }

    /// <summary>Bytes and characters are different questions, and the gutter answers both.</summary>
    [Fact]
    public void Selection_ReportsBytesAndCharacters()
    {
        // "日本語" is 9 bytes, 3 characters.
        var readout = Describe("a日本語b", 1, new RawSelection(1, 10));

        Assert.Equal(9, readout.SelectionBytes);
        Assert.Equal(3, readout.SelectionCharacters);
    }

    /// <summary>An invalid run counts as the one U+FFFD the rows draw for it, not as its bytes.</summary>
    [Fact]
    public void Selection_CountsAnInvalidRunTheWayItIsDrawn()
    {
        var readout = Describe([(byte)'a', 0xE2, 0x82, (byte)'b'], 0, new RawSelection(0, 4));

        Assert.Equal(4, readout.SelectionBytes);
        Assert.Equal(3, readout.SelectionCharacters);
    }

    /// <summary>
    /// Select-all on a multi-GB file is one keystroke. Bytes are free (two offsets subtracted);
    /// characters would be a full decode, so past the cap they are not offered.
    /// </summary>
    [Fact]
    public void Selection_LargerThanTheCap_ReportsBytesOnly()
    {
        byte[] content = Encoding.UTF8.GetBytes(new string('x', RawCaretReadout.SelectionScanBytes + 1_000));
        var readout = Describe(content, 0, new RawSelection(0, content.Length));

        Assert.Equal(content.Length, readout.SelectionBytes);
        Assert.Null(readout.SelectionCharacters);
    }
}
