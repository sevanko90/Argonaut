using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// The decoder must produce exactly the text <see cref="RawRowReader"/> already produces - it is
/// the same row, drawn the same way - while also saying which byte each character came from. The
/// text equivalence is asserted differentially against the reader over the same mixed corpus
/// RawSegmentIndexTests uses, because the two decode paths agreeing by construction is the whole
/// point: a caret that positions against a different string than the one on screen is useless.
/// </summary>
public class RawRowDecoderTests
{
    private const int ReplacementChar = 0xFFFD;

    private static RawDecodedRow DecodeBytes(byte[] content, bool isSoftWrapped = false)
        => RawRowDecoder.Decode(new ArrayByteSource(content), 0, content.Length, isSoftWrapped);

    private static RawDecodedRow Decode(string text, bool isSoftWrapped = false)
        => DecodeBytes(Encoding.UTF8.GetBytes(text), isSoftWrapped);

    [Fact]
    public void Ascii_MapsOneCharToOneByte()
    {
        var row = Decode("abc");

        Assert.Equal("abc", row.Text);
        Assert.Equal(3, row.DisplayByteLength);
        Assert.Equal(0, row.ByteOffsetForChar(0));
        Assert.Equal(1, row.ByteOffsetForChar(1));
        Assert.Equal(2, row.ByteOffsetForChar(2));
        Assert.Equal(3, row.ByteOffsetForChar(3)); // end-of-row caret position
    }

    [Fact]
    public void MultiByteCharacters_MapToTheirFirstByte()
    {
        // 'é' is two bytes in UTF-8, so the chars are at byte 0, 2 and 4.
        var row = Decode("ééé");

        Assert.Equal("ééé", row.Text);
        Assert.Equal(6, row.DisplayByteLength);
        Assert.Equal(0, row.ByteOffsetForChar(0));
        Assert.Equal(2, row.ByteOffsetForChar(1));
        Assert.Equal(4, row.ByteOffsetForChar(2));
        Assert.Equal(6, row.ByteOffsetForChar(3));
    }

    [Fact]
    public void AstralCharacter_IsTwoCharsSharingOneByteOffset()
    {
        // U+1F600 is four UTF-8 bytes and one surrogate pair - two chars, one character.
        var row = Decode("\U0001F600!");

        Assert.Equal(3, row.Text.Length);
        Assert.Equal(0, row.ByteOffsetForChar(0));
        Assert.Equal(0, row.ByteOffsetForChar(1)); // low surrogate belongs to the same byte
        Assert.Equal(4, row.ByteOffsetForChar(2)); // the '!'
        Assert.Equal(5, row.ByteOffsetForChar(3));

        // The caret may sit before or after the emoji, never between its surrogates.
        Assert.True(row.IsCharacterBoundary(0));
        Assert.False(row.IsCharacterBoundary(1));
        Assert.False(row.IsCharacterBoundary(3));
        Assert.True(row.IsCharacterBoundary(4));
    }

    [Fact]
    public void InvalidBytes_CollapseToOneReplacementCharPerRun()
    {
        // 0xC3 starts a two-byte sequence; 0x28 does not continue it, so 0xC3 alone is the
        // maximal invalid subpart and '(' decodes normally after it.
        var row = DecodeBytes([0x61, 0xC3, 0x28, 0x62]);

        Assert.Equal(4, row.Text.Length);
        Assert.Equal('a', row.Text[0]);
        Assert.Equal(ReplacementChar, row.Text[1]);
        Assert.Equal('(', row.Text[2]);
        Assert.Equal('b', row.Text[3]);
        Assert.Equal(1, row.ByteOffsetForChar(1));
        Assert.Equal(2, row.ByteOffsetForChar(2));
    }

    [Fact]
    public void TruncatedSequenceAtRowEnd_IsOneReplacementChar()
    {
        // A four-byte sequence cut short - what binary content does at a forced break.
        var row = DecodeBytes([0x61, 0xF0, 0x9F, 0x98]);

        Assert.Equal(2, row.Text.Length);
        Assert.Equal('a', row.Text[0]);
        Assert.Equal(ReplacementChar, row.Text[1]);
        Assert.Equal(1, row.ByteOffsetForChar(1));
        Assert.Equal(4, row.ByteOffsetForChar(2));

        // The caret cannot land inside the truncated run, so deleting removes it whole.
        Assert.True(row.IsCharacterBoundary(1));
        Assert.False(row.IsCharacterBoundary(2));
        Assert.False(row.IsCharacterBoundary(3));
        Assert.True(row.IsCharacterBoundary(4));
    }

    [Fact]
    public void ControlBytes_BecomeControlPicturesWithoutMovingOffsets()
    {
        var row = DecodeBytes([0x61, 0x00, 0x07, 0x7F, 0x62]);

        Assert.Equal("a␀␇␡b", row.Text);
        for (int i = 0; i <= 5; i++)
            Assert.Equal(i, row.ByteOffsetForChar(i));
    }

    [Fact]
    public void Tab_PassesThrough()
    {
        var row = Decode("a\tb");

        Assert.Equal("a\tb", row.Text);
    }

    [Theory]
    [InlineData("line\n", 4)]
    [InlineData("line\r\n", 4)]
    public void RealLineEnd_HasItsNewlineExcludedFromTheDisplayBytes(string text, int expectedDisplayBytes)
    {
        var row = Decode(text);

        Assert.Equal("line", row.Text);
        Assert.Equal(expectedDisplayBytes, row.DisplayByteLength);
        Assert.Equal(expectedDisplayBytes, row.ByteOffsetForChar(row.Text.Length));
    }

    [Fact]
    public void SoftWrappedRow_KeepsEveryByte()
    {
        // A soft-wrapped segment never ends in a newline, so nothing is trimmed.
        var row = Decode("abc", isSoftWrapped: true);

        Assert.Equal("abc", row.Text);
        Assert.Equal(3, row.DisplayByteLength);
    }

    [Fact]
    public void EmptyRow_HasOnlyTheEndPosition()
    {
        var row = Decode(string.Empty);

        Assert.Equal(string.Empty, row.Text);
        Assert.Equal(0, row.DisplayByteLength);
        Assert.Equal(0, row.ByteOffsetForChar(0));
        Assert.True(row.IsCharacterBoundary(0));
    }

    [Fact]
    public void CharIndexForByte_ReportsTheCharacterContainingTheByte()
    {
        var row = Decode("aéb"); // bytes: a=0, é=1..2, b=3

        Assert.Equal(0, row.CharIndexForByte(0));
        Assert.Equal(1, row.CharIndexForByte(1));
        Assert.Equal(1, row.CharIndexForByte(2)); // interior byte reports its own character
        Assert.Equal(2, row.CharIndexForByte(3));
        Assert.Equal(3, row.CharIndexForByte(4)); // end of row
    }

    [Fact]
    public void CharIndexForByte_AndByteOffsetForChar_RoundTrip()
    {
        var row = Decode("aéb\U0001F600c");

        for (int charIndex = 0; charIndex <= row.Text.Length; charIndex++)
        {
            int byteOffset = row.ByteOffsetForChar(charIndex);
            int backToChar = row.CharIndexForByte(byteOffset);

            // A surrogate pair shares a byte offset, so the round trip lands on the pair's first
            // char - which is the caret position that byte offset means.
            Assert.Equal(row.ByteOffsetForChar(backToChar), byteOffset);
        }
    }

    [Fact]
    public void OutOfRangeCharIndex_Throws()
    {
        var row = Decode("abc");

        Assert.Throws<ArgumentOutOfRangeException>(() => row.ByteOffsetForChar(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => row.ByteOffsetForChar(4));
    }

    [Fact]
    public void DecodingAcrossPieceBoundaries_MatchesDecodingContiguousBytes()
    {
        // The gather path: a piece table splits the row, so Decode must stitch it back before
        // decoding - a multi-byte character split across the seam is the case that would break.
        byte[] content = Encoding.UTF8.GetBytes("aé😀b");
        var table = new RawPieceTable(new ArrayByteSource([]));
        table.Insert(0, content.AsSpan(0, 3));
        table.Insert(3, content.AsSpan(3));
        Assert.True(table.PieceCount > 1);

        var split = RawRowDecoder.Decode(table, 0, table.Length, isSoftWrapped: true);
        var whole = DecodeBytes(content, isSoftWrapped: true);

        Assert.Equal(whole.Text, split.Text);
        Assert.Equal(whole.DisplayByteLength, split.DisplayByteLength);
        for (int i = 0; i <= whole.Text.Length; i++)
            Assert.Equal(whole.ByteOffsetForChar(i), split.ByteOffsetForChar(i));
    }

    /// <summary>
    /// The load-bearing test: over several MB of mixed content, indexed into real display rows,
    /// the decoder's text must equal the reader's for every row. If these ever diverge the caret
    /// is positioning against a string the view is not drawing.
    /// </summary>
    [Fact]
    public void EveryRowOfAMixedFile_DecodesToTheSameTextAsTheReader()
    {
        var rng = new Random(12345);
        var sb = new StringBuilder();
        while (sb.Length < 2 * 1024 * 1024)
        {
            int lineLength = rng.Next(0, 2000);
            sb.Append(rng.Next(4) == 0 ? new string('é', lineLength / 2) : new string('x', lineLength));
            sb.Append(rng.Next(8) == 0 ? "\r\n" : "\n");
        }
        sb.Append("last line without newline");

        byte[] content = Encoding.UTF8.GetBytes(sb.ToString());
        AssertReaderAndDecoderAgree(content, wrapWidth: 512);
    }

    /// <summary>
    /// The same equivalence over bytes that are not text at all - invalid sequences, control
    /// bytes and forced breaks landing mid-character are exactly where two decoders drift apart.
    /// </summary>
    [Fact]
    public void EveryRowOfBinaryContent_DecodesToTheSameTextAsTheReader()
    {
        var rng = new Random(999);
        byte[] content = new byte[512 * 1024];
        rng.NextBytes(content);

        // Sprinkle in newlines so the file has real line ends as well as forced breaks.
        for (int i = 0; i < content.Length; i += rng.Next(1, 300))
            content[i] = (byte)'\n';

        AssertReaderAndDecoderAgree(content, wrapWidth: 80);
    }

    private static void AssertReaderAndDecoderAgree(byte[] content, int wrapWidth)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            using var file = new MMapFile(path);
            var index = RawSegmentIndex.StartIndexing(file, wrapWidth);
            index.IndexingTask.GetAwaiter().GetResult();

            Assert.True(index.RowCount > 1000, $"expected a lot of rows, got {index.RowCount}");

            for (int rowIndex = 0; rowIndex < index.RowCount; rowIndex++)
            {
                var info = index.GetRowInfo(rowIndex);
                string expected = RawRowReader.ReadRow(file, info.Start, info.End, info.IsSoftWrapped);
                var decoded = RawRowDecoder.Decode(file, info.Start, info.End, info.IsSoftWrapped);

                Assert.Equal(expected, decoded.Text);

                // The map must stay inside the row and never run backwards.
                int previous = -1;
                for (int charIndex = 0; charIndex <= decoded.Text.Length; charIndex++)
                {
                    int offset = decoded.ByteOffsetForChar(charIndex);
                    Assert.InRange(offset, 0, decoded.DisplayByteLength);
                    Assert.True(offset >= previous, $"row {rowIndex} char {charIndex} went backwards");
                    previous = offset;
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
