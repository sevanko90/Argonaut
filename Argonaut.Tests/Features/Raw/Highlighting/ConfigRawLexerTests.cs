using Argonaut.Features.Raw.Highlighting;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Raw.Highlighting;

/// <summary>
/// The one lexer behind YAML and the <c>key = value</c> formats: each format's own shapes, plus the
/// rule that keeps them apart where they could collide - a colon only separates when a blank or
/// the line's end follows it.
/// </summary>
public sealed class ConfigRawLexerTests
{
    private static readonly ConfigRawLexer Lexer = new();

    private static List<(string Text, RawTextStyle Style)> Pieces(string row, RawLexState entry = default)
    {
        var spans = RawLexTesting.Lex(Lexer, row, entry).Spans;
        return spans.Select(s => (row.Substring(s.Start, s.Length), s.Style)).ToList();
    }

    [Fact]
    public void CommentLine_AndTrailingComment()
    {
        Assert.Equal(new[] { ("# note", RawTextStyle.Comment) }, Pieces("  # note"));
        Assert.Equal(("# why", RawTextStyle.Comment), Pieces("a: 1 # why")[^1]);
    }

    [Fact]
    public void HashWithoutPrecedingSpace_IsPartOfTheValue()
        => Assert.Equal(("a#b", RawTextStyle.String), Pieces("k: a#b")[^1]);

    [Theory]
    [InlineData("---")]
    [InlineData("...")]
    public void DocumentMarkers_ArePunctuation(string marker)
        => Assert.Equal(new[] { (marker, RawTextStyle.Punctuation) }, Pieces(marker));

    [Fact]
    public void ListMarkers_ArePunctuationEvenWhenRepeated()
        => Assert.Equal(
            new[]
            {
                ("-", RawTextStyle.Punctuation),
                ("-", RawTextStyle.Punctuation),
                ("a", RawTextStyle.String),
            },
            Pieces("- - a"));

    [Fact]
    public void KeyAndValue()
        => Assert.Equal(
            new[] { ("name", RawTextStyle.Key), (":", RawTextStyle.Punctuation), ("Marc", RawTextStyle.String) },
            Pieces("name: Marc"));

    [Fact]
    public void KeyWithNothingAfterTheColon_StartsAMapping()
        => Assert.Equal(
            new[] { ("server", RawTextStyle.Key), (":", RawTextStyle.Punctuation) },
            Pieces("  server:"));

    [Fact]
    public void ListItemWithKey()
        => Assert.Equal(
            new[]
            {
                ("-", RawTextStyle.Punctuation),
                ("id", RawTextStyle.Key),
                (":", RawTextStyle.Punctuation),
                ("7", RawTextStyle.Number),
            },
            Pieces("- id: 7"));

    [Fact]
    public void QuotedKey_AndQuotedValue()
        => Assert.Equal(
            new[]
            {
                ("\"a b\"", RawTextStyle.Key),
                (":", RawTextStyle.Punctuation),
                ("'x: y'", RawTextStyle.String),
            },
            Pieces("\"a b\": 'x: y'"));

    [Fact]
    public void ValueKinds()
    {
        Assert.Contains(("true", RawTextStyle.Keyword), Pieces("a: true"));
        Assert.Contains(("No", RawTextStyle.Keyword), Pieces("a: No"));
        Assert.Contains(("null", RawTextStyle.Literal), Pieces("a: null"));
        Assert.Contains(("~", RawTextStyle.Literal), Pieces("a: ~"));
        Assert.Contains(("-3.5", RawTextStyle.Number), Pieces("a: -3.5"));
        Assert.Contains(("|", RawTextStyle.Punctuation), Pieces("a: |"));
        Assert.Contains((">-", RawTextStyle.Punctuation), Pieces("a: >-"));
        Assert.Contains(("http://x.org/a:b", RawTextStyle.String), Pieces("u: http://x.org/a:b"));
    }

    [Fact]
    public void BlockScalarBody_IsNeverAKey()
    {
        var pieces = Pieces("    just some text");
        Assert.DoesNotContain(pieces, p => p.Style == RawTextStyle.Key);
        Assert.Contains(("just", RawTextStyle.String), pieces);
    }

    [Fact]
    public void PlainListItem_IsAValue()
        => Assert.Equal(
            new[] { ("-", RawTextStyle.Punctuation), ("true", RawTextStyle.Keyword) },
            Pieces("- true"));

    [Fact]
    public void IniSection_ColoursTheBrackets()
        => Assert.Equal(new[] { ("[core]", RawTextStyle.Section) }, Pieces("[core]"));

    [Fact]
    public void TomlArrayTable_ColoursBothBracketPairs()
        => Assert.Equal(new[] { ("[[servers]]", RawTextStyle.Section) }, Pieces("[[servers]]"));

    [Fact]
    public void BracketAfterAListMarker_IsAValueNotASection()
        => Assert.DoesNotContain(Pieces("- [a, b]"), p => p.Style == RawTextStyle.Section);

    [Theory]
    [InlineData("; note")]
    [InlineData("# note")]
    [InlineData("  ! note")]
    public void CommentLine_IsAllComment(string line)
        => Assert.Equal(new[] { (line.TrimStart(), RawTextStyle.Comment) }, Pieces(line));

    [Fact]
    public void EqualsSeparatesAKey()
        => Assert.Equal(
            new[] { ("name", RawTextStyle.Key), ("=", RawTextStyle.Punctuation), ("Marc", RawTextStyle.String) },
            Pieces("name=Marc"));

    [Fact]
    public void EnvLine_WithExport()
        => Assert.Equal(
            new[]
            {
                ("export", RawTextStyle.Keyword),
                ("FOO", RawTextStyle.Key),
                ("=", RawTextStyle.Punctuation),
                ("bar", RawTextStyle.String),
            },
            Pieces("export FOO=bar"));

    [Fact]
    public void TomlLine_WithTrailingComment()
        => Assert.Equal(
            new[]
            {
                ("key", RawTextStyle.Key),
                ("=", RawTextStyle.Punctuation),
                ("\"x\"", RawTextStyle.String),
                ("# note", RawTextStyle.Comment),
            },
            Pieces("key = \"x\" # note"));

    [Fact]
    public void QuotedKeyBeforeEquals()
        => Assert.Equal(
            new[] { ("\"a b\"", RawTextStyle.Key), ("=", RawTextStyle.Punctuation), ("1", RawTextStyle.Number) },
            Pieces("\"a b\" = 1"));

    [Fact]
    public void SemicolonAfterABlank_StartsATrailingComment()
        => Assert.Equal(
            new[]
            {
                ("home", RawTextStyle.Key),
                ("=", RawTextStyle.Punctuation),
                ("http://x.org/#top", RawTextStyle.String),
                ("; note", RawTextStyle.Comment),
            },
            Pieces("home=http://x.org/#top ; note"));

    [Theory]
    [InlineData("time = 12:30", "12:30")]
    [InlineData("path = C:\\dir", "C:\\dir")]
    [InlineData("url = http://x.org", "http://x.org")]
    public void ColonInsideAnEqualsValue_IsPartOfTheValue(string line, string value)
        => Assert.Contains((value, RawTextStyle.String), Pieces(line));

    [Theory]
    [InlineData("http://x.org/a")]
    [InlineData("C:\\dir\\file")]
    [InlineData("at 12:30 sharp")]
    public void ColonWithTextStraightAfter_IsNotASeparator(string line)
        => Assert.DoesNotContain(Pieces(line), p => p.Style == RawTextStyle.Key);

    [Fact]
    public void ColonBeforeEquals_IsTheSeparator()
        => Assert.Equal(("a", RawTextStyle.Key), Pieces("a: x=y")[0]);

    [Fact]
    public void IniKeywordsAndYesterday()
    {
        Assert.Contains(("TRUE", RawTextStyle.Keyword), Pieces("b = TRUE"));
        Assert.Contains(("off", RawTextStyle.Keyword), Pieces("b: off"));
        Assert.Contains(("'x # y'", RawTextStyle.String), Pieces("s = 'x # y'"));
        Assert.Contains(("yesterday", RawTextStyle.String), Pieces("s = yesterday"));
    }

    [Fact]
    public void RowContinuingAKeyValueLine_KeepsItsPhase()
    {
        var (_, exit) = RawLexTesting.Lex(Lexer, "key = \"open", RawLexState.LineStart);
        var (spans, _) = RawLexTesting.Lex(Lexer, "ed\" ; c", exit);
        Assert.Equal(new RawStyledSpan(0, 3, RawTextStyle.String), spans[0]);
        Assert.Equal(RawTextStyle.Comment, spans[1].Style);
    }

    public static IEnumerable<object[]> SampleLines() => new[]
    {
        "# a comment",
        "--- # doc",
        "id: Marc Evans",
        "  - id: 7  # seven",
        "- - child: \"q # not\" # real",
        "\"quoted key\"  : 'single: value'",
        "empty:",
        "debug: true # yes",
        "empty2: ~",
        "body: |",
        "url: http://x.org/a#b",
        "size: -1.5e3 apples",
        "plain text with no key # but a comment",
        "\tkey\t:\tv",
        "[core]",
        "  [section name] ; trailing",
        "; a comment line",
        "export FOO=\"bar baz\" # why",
        "key = \"x\" # note",
        "home=http://x.org/#top ; note",
        "my long key = 'single # quoted' ; c",
        "time = 12:30 ; noon",
        "flag = true # on",
        "path = \"C:\\\\dir\\\\\\\"q\" ; esc",
        "key   =   ",
        "\tkey\t=\tvalue\t# tab",
    }.Select(line => new object[] { line });

    [Theory]
    [MemberData(nameof(SampleLines))]
    public void SplitAtAnyPosition_ColoursLikeTheWholeLine(string line)
    {
        int separator = FirstSeparator(line);

        // Cuts whose difference is a known property of colouring a row at a time, not a bug:
        // a key cut before its separator reads as value text on the first part; a value word cut
        // part-way may read as the keyword or number it starts; a document marker and the
        // "export" prefix are only recognised whole.
        bool SkipCut(int k)
        {
            if (line.StartsWith("---") && k is > 0 and < 3)
                return true;
            if (line.StartsWith("export ") && k < 7)
                return true;
            if (separator >= 0 && k <= separator)
                return true;
            if (k <= 0 || k >= line.Length || char.IsWhiteSpace(line[k - 1]) || char.IsWhiteSpace(line[k]))
                return false;
            int start = k;
            while (start > 0 && char.IsAsciiLetter(line[start - 1]))
                start--;
            return separator >= 0 && start > separator;
        }

        RawLexTesting.AssertSplitsAgree(Lexer, line, RawTextStyle.String, SkipCut);
    }

    /// <summary>Where the line's first separator is, by the lexer's rule, or -1.</summary>
    private static int FirstSeparator(string line)
    {
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '=' || (line[k] == ':' && (k + 1 == line.Length || line[k + 1] is ' ' or '\t')))
                return k;
        }

        return -1;
    }

    [Fact]
    public void KeyCutBeforeItsColon_IsAValueOnTheFirstPart()
    {
        var (_, exit) = RawLexTesting.Lex(Lexer, "longkey", RawLexState.LineStart);
        var (spans, _) = RawLexTesting.Lex(Lexer, "name: v", exit);
        Assert.Equal(new RawStyledSpan(0, 4, RawTextStyle.Key), spans[0]);
    }

    [Fact]
    public void Lex_DoesNotAllocate()
    {
        const string row = "  - key: \"value\" # a note about 12 things yes";
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
