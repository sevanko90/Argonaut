using System.Text;
using Argonaut.Features.Raw;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Double-click word selection. The interesting cases are all places where a "word" and the bytes
/// it occupies disagree: a word split by the wrap width, a multi-byte character, and the display
/// substitutions that stand for bytes which are not text at all.
/// </summary>
public class RawWordStopsTests
{
    private static (RawSegmentIndex Index, IByteSource Source) Indexed(byte[] content, int wrapWidth = 80)
    {
        var source = new ArrayByteSource(content);
        var index = RawSegmentIndex.StartIndexing(source, wrapWidth);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, source);
    }

    private static (RawSegmentIndex Index, IByteSource Source) Indexed(string text, int wrapWidth = 80)
        => Indexed(Encoding.UTF8.GetBytes(text), wrapWidth);

    private static (long Start, long End) WordAt(string text, long offset, int wrapWidth = 80)
    {
        var (index, source) = Indexed(text, wrapWidth);
        var word = RawWordStops.WordAt(index, source, offset);
        Assert.NotNull(word);
        return word.Value;
    }

    [Fact]
    public void Word_SelectsTheRunOfLettersAroundTheClick()
    {
        Assert.Equal((4, 9), WordAt("the quick brown fox", 6));
    }

    [Fact]
    public void Word_TakesDigitsAndUnderscoresWithIt()
    {
        Assert.Equal((0, 8), WordAt("foo_bar1 baz", 3));
    }

    [Fact]
    public void Whitespace_SelectsTheRunOfSpaces()
    {
        Assert.Equal((1, 5), WordAt("a    b", 3));
    }

    [Fact]
    public void Punctuation_SelectsTheRunOfPunctuation()
    {
        Assert.Equal((1, 3), WordAt("a==b", 1));
    }

    [Fact]
    public void ClickAtTheEndOfTheDocument_SelectsTheWordEndingThere()
    {
        Assert.Equal((0, 3), WordAt("foo", 3));
    }

    [Fact]
    public void WordDoesNotRunPastARealLineEnd()
    {
        Assert.Equal((4, 7), WordAt("abc\ndef\nghi", 5));
    }

    [Fact]
    public void WordStraddlingASoftWrap_SelectsTheWholeRun()
    {
        // Wrapped every 4 bytes, so the word is spread over two rows with no newline between.
        Assert.Equal((0, 8), WordAt("abcdefgh", 2, wrapWidth: 4));
    }

    [Fact]
    public void WordStraddlingSeveralSoftWraps_SelectsTheWholeRun()
    {
        Assert.Equal((1, 13), WordAt(" abcdefghijkl ", 6, wrapWidth: 4));
    }

    [Fact]
    public void MultibyteCharacters_ReportByteBoundsNotCharacterBounds()
    {
        // "héllo" is 6 bytes: the 'é' is two. Clicking anywhere in it selects the whole word.
        Assert.Equal((0, 6), WordAt("héllo wörld", 2));
    }

    [Fact]
    public void AstralCharacters_AreOneThing()
    {
        // A surrogate pair reports one byte offset from both halves; clicking it must not split
        // the run at the pair.
        Assert.Equal((0, 6), WordAt("a\U0001F600b", 2));
    }

    [Fact]
    public void InvalidByteRun_SelectsOnlyThatRun()
    {
        var (index, source) = Indexed([(byte)'a', 0xE2, 0x82, (byte)'b']);

        // 0xE2 0x82 is a truncated three-byte sequence: one U+FFFD standing for both bytes, and
        // a corrupt run is a thing to select on its own, never part of the word beside it.
        Assert.Equal((1, 3), RawWordStops.WordAt(index, source, 1));
    }

    [Fact]
    public void ControlCharacter_SelectsOnlyItself()
    {
        var (index, source) = Indexed([(byte)'a', 0x01, (byte)'b']);

        Assert.Equal((1, 2), RawWordStops.WordAt(index, source, 1));
    }

    /// <summary>
    /// The separator substitutions are opaque for the same reason a Control Picture is: the glyph
    /// stands for bytes the row does not otherwise show, so it selects alone rather than joining
    /// the text either side of it. LS here is three bytes, at offsets 1 to 4.
    /// </summary>
    [Fact]
    public void UnicodeSeparator_SelectsOnlyItself()
    {
        var (index, source) = Indexed("a\u2028b");

        Assert.Equal((1, 4), RawWordStops.WordAt(index, source, 1));
    }

    [Fact]
    public void RunLongerThanTheCap_IsRefused()
    {
        var (index, source) = Indexed(new string('a', RawWordStops.MaxWordBytes + 1000));

        Assert.Null(RawWordStops.WordAt(index, source, 0));
        Assert.Null(RawWordStops.WordAt(index, source, source.AvailableLength / 2));
    }

    [Fact]
    public void RunAtTheCap_IsSelected()
    {
        var (index, source) = Indexed(new string('a', RawWordStops.MaxWordBytes));

        Assert.Equal((0, RawWordStops.MaxWordBytes), RawWordStops.WordAt(index, source, 0));
    }

    [Fact]
    public void EmptyRow_PlacesTheCaretRatherThanSelecting()
    {
        Assert.Equal((4, 4), WordAt("abc\n\ndef", 4));
    }

    [Fact]
    public void SelectWordAt_PutsTheCaretAtTheEndOfTheWord()
    {
        var (index, source) = Indexed("the quick brown fox");
        var caret = new RawCaretController(index, source);

        Assert.True(caret.SelectWordAt(6));

        Assert.Equal(4, caret.Selection.Start);
        Assert.Equal(9, caret.Selection.End);
        Assert.Equal(9, caret.Caret.Offset);
    }

    [Fact]
    public void SelectWordAt_RefusesAndLeavesTheCaretAloneWhenTheRunIsTooLong()
    {
        var (index, source) = Indexed(new string('a', RawWordStops.MaxWordBytes + 1000));
        var caret = new RawCaretController(index, source);
        caret.PlaceAt(10);

        Assert.False(caret.SelectWordAt(10));

        Assert.Equal(10, caret.Caret.Offset);
        Assert.True(caret.Selection.IsEmpty);
    }
}
