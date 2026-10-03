using Argonaut.Features.Raw.Highlighting;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw.Highlighting;

public sealed class JsonRawLexerTests
{
    private static readonly JsonRawLexer Lexer = new();

    private static List<(string Text, RawTextStyle Style)> Pieces(string row, RawLexState entry = default)
    {
        var spans = RawLexTesting.Lex(Lexer, row, entry).Spans;
        return spans.Select(s => (row.Substring(s.Start, s.Length), s.Style)).ToList();
    }

    [Fact]
    public void ObjectMember_ColoursKeyValueAndPunctuation()
    {
        Assert.Equal(
            new[]
            {
                ("{", RawTextStyle.Punctuation),
                ("\"name\"", RawTextStyle.Key),
                (":", RawTextStyle.Punctuation),
                ("\"x\"", RawTextStyle.String),
                (",", RawTextStyle.Punctuation),
                ("\"n\"", RawTextStyle.Key),
                (":", RawTextStyle.Punctuation),
                ("12", RawTextStyle.Number),
                ("}", RawTextStyle.Punctuation),
            },
            Pieces("{\"name\": \"x\", \"n\" : 12}"));
    }

    [Fact]
    public void NestedArray_ColoursEachBracket()
    {
        var pieces = Pieces("[[1,2],[true]]");
        Assert.Equal(8, pieces.Count(p => p.Style == RawTextStyle.Punctuation));
        Assert.Contains(("true", RawTextStyle.Keyword), pieces);
    }

    [Fact]
    public void EscapedQuoteInsideString_DoesNotEndIt()
    {
        Assert.Equal(new[] { ("\"a\\\"b\"", RawTextStyle.String) }, Pieces("\"a\\\"b\""));
    }

    [Fact]
    public void Numbers_IncludeExponentAndSign()
    {
        Assert.Equal(new[] { ("-1.5e+10", RawTextStyle.Number) }, Pieces("-1.5e+10"));
    }

    [Fact]
    public void Keywords_AreWholeWordsOnly()
    {
        Assert.Equal(
            new[] { ("true", RawTextStyle.Keyword), ("false", RawTextStyle.Keyword), ("null", RawTextStyle.Literal) },
            Pieces("true false null"));
        Assert.Empty(Pieces("nullable truex xtrue"));
    }

    [Fact]
    public void LineComment_RunsToTheRowEndAndIsCarried()
    {
        var (spans, exit) = RawLexTesting.Lex(Lexer, "1 // note", RawLexState.LineStart);
        Assert.Equal(new RawStyledSpan(2, 7, RawTextStyle.Comment), spans[^1]);

        var (next, _) = RawLexTesting.Lex(Lexer, "still \"comment\"", exit);
        Assert.Equal(new[] { new RawStyledSpan(0, 15, RawTextStyle.Comment) }, next);
    }

    [Fact]
    public void BlockComment_ClosedOnTheRow_EndsThere()
    {
        Assert.Equal(
            new[] { ("/* a */", RawTextStyle.Comment), ("1", RawTextStyle.Number) },
            Pieces("/* a */ 1"));
    }

    [Fact]
    public void BlockComment_Unclosed_ColoursTheRestOfTheLine()
    {
        var (spans, exit) = RawLexTesting.Lex(Lexer, "1 /* open", RawLexState.LineStart);
        Assert.Equal(new RawStyledSpan(2, 7, RawTextStyle.Comment), spans[^1]);

        var (next, _) = RawLexTesting.Lex(Lexer, " more */ 2", exit);
        Assert.Equal(new RawStyledSpan(0, 8, RawTextStyle.Comment), next[0]);
        Assert.Equal(new RawStyledSpan(9, 1, RawTextStyle.Number), next[1]);
    }

    [Fact]
    public void NextLine_StartsClean()
    {
        // The state a line ends in is not what a line start is given: the caller passes
        // LineStart, so a comment left open does not leak into the next line.
        var (spans, _) = RawLexTesting.Lex(Lexer, "\"a\"", RawLexState.LineStart);
        Assert.Equal(RawTextStyle.String, spans[0].Style);
    }

    public static IEnumerable<object[]> SampleLines() => new[]
    {
        "{\"name\": \"value\", \"n\" : 12.5e-3, \"ok\": true, \"gone\": null}",
        "[[1,2],[\"a\\\"b\", false],{}]",
        "  \"key with \\\\ escapes \\\" inside\"  :  [ -1, 2e+5, 0.5 ]",
        "{\"a\": 1} // trailing comment with \"quotes\" and 12",
        "{\"a\": /* inline */ 2, \"b\": /* open at the end",
        "/* lead */ [true,false,null]/",
        "\t\"tab\"\t:\t1",
    }.Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(SampleLines))]
    public void SplitAtAnyPosition_ColoursLikeTheWholeLine(string line)
        => RawLexTesting.AssertSplitsAgree(Lexer, line);

    [Fact]
    public void RowStartingInsideAString_StartsString()
    {
        var (_, exit) = RawLexTesting.Lex(Lexer, "\"abc", RawLexState.LineStart);
        var (spans, _) = RawLexTesting.Lex(Lexer, "def\", 1", exit);
        Assert.Equal(new RawStyledSpan(0, 4, RawTextStyle.String), spans[0]);
    }

    [Fact]
    public void RowEndingOnABackslash_EscapesTheNextRowsFirstChar()
    {
        var (_, exit) = RawLexTesting.Lex(Lexer, "\"abc\\", RawLexState.LineStart);
        var (spans, _) = RawLexTesting.Lex(Lexer, "\"def\" ", exit);
        Assert.Equal(new RawStyledSpan(0, 5, RawTextStyle.String), spans[0]);
    }

    [Fact]
    public void Lex_DoesNotAllocate()
    {
        const string row = "{\"name\": \"value\", \"n\": 12.5, \"ok\": true, \"x\": null} // c";
        var spans = new List<RawStyledSpan>(64);
        Lexer.Lex(row, RawLexState.LineStart, spans);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            spans.Clear();
            Lexer.Lex(row, RawLexState.LineStart, spans);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
