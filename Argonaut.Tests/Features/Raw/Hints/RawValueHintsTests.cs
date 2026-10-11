using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Hints;
using Argonaut.Features.Raw.Highlighting;
using Argonaut.Features.Raw.Hints;
using Argonaut.Features.Raw.Rows;
using Argonaut.Ui.Hints;

namespace Argonaut.Tests.Features.Raw.Hints;

/// <summary>
/// The raw view's value hints over rows the way the view lays them out: each line cut into rows
/// of a fixed width, lexed row by row with the state carried, and the hints' carry with it.
/// </summary>
public sealed class RawValueHintsTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.c2ln";

    internal sealed record MarkedRow(string Text, List<RawHintMark> Marks)
    {
        public IEnumerable<string> Marked => Marks.Select(m => Text[m.Start..m.End]);
    }

    internal static List<MarkedRow> Mark(string document, int width = 200, IRawLexer? lexer = null, DateHintSettings? dates = null, bool noLexer = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(document);
        var source = new MemoryByteSource(bytes);
        dates ??= new DateHintSettings();
        var hints = new RawValueHints(
            [new DateHintProvider(dates), new IsoDateHintProvider(dates, TimeProvider.System), new ColourHintProvider(),
             new JwtHintProvider(TimeProvider.System), new UrlHintProvider(), new CronHintProvider(dates, TimeProvider.System), new Base64HintProvider()], dates)
        {
            Source = source,
        };
        lexer ??= noLexer ? null : new JsonRawLexer();

        var rows = new List<MarkedRow>();
        long lineStart = 0;
        foreach (string line in document.Split('\n'))
        {
            var lexState = RawLexState.LineStart;
            var carry = RawHintCarry.None;
            int lineBytes = Encoding.UTF8.GetByteCount(line);
            for (int at = 0; at == 0 || at < lineBytes; at += width)
            {
                int end = Math.Min(at + width, lineBytes);
                bool wraps = end < lineBytes;
                var decoded = RawRowDecoder.Decode(source, lineStart + at, lineStart + end, wraps);
                var marks = new List<RawHintMark>();
                var hintRow = new RawHintRow(decoded, at == 0, wraps);
                if (lexer is null)
                {
                    hints.MarkWords(hintRow, null, marks);
                }
                else
                {
                    var spans = new List<RawStyledSpan>();
                    lexState = lexer.Lex(decoded.Text, lexState, spans);
                    carry = hints.MarkRow(hintRow, carry, spans, marks);
                    hints.MarkWords(hintRow, spans, marks);
                }
                rows.Add(new MarkedRow(decoded.Text, marks));
            }

            lineStart += lineBytes + 1;
        }

        return rows;
    }

    [Fact]
    public void APrettyPrintedMember_IsAChipAtTheEndOfItsLine()
    {
        var rows = Mark("{\n  \"colour\": \"#336699\",\n  \"n\": 1\n}");

        var mark = Assert.Single(rows[1].Marks);
        Assert.True(mark.IsChip);
        Assert.Equal("#336699", rows[1].Marked.Single());
        Assert.Equal(ValueHintKind.Colour, mark.Hint.Kind);
        Assert.Empty(rows[2].Marks);
    }

    [Fact]
    public void AMinifiedLine_MarksEachValueInPlace()
    {
        var rows = Mark("{\"c\":\"#336699\",\"u\":\"https://example.com/x\",\"n\":1}");

        Assert.Equal(["#336699", "https://example.com/x"], rows[0].Marked);
        Assert.All(rows[0].Marks, m => Assert.False(m.IsChip));
    }

    [Fact]
    public void AKeyIsNeverHinted()
        => Assert.Empty(Mark("{\"https://example.com\": 1}")[0].Marks);

    [Fact]
    public void AValueWrappedOverRows_IsReadWhole_AndIsAChip_EndingOnItsLastRow()
    {
        string url = "https://example.com/" + new string('a', 40);
        var rows = Mark($"  \"u\": \"{url}\",", width: 16);

        Assert.Equal(url, string.Concat(rows.SelectMany(r => r.Marked)));
        var marked = rows.SkipWhile(r => r.Marks.Count == 0).ToList();
        Assert.All(marked, r => Assert.True(Assert.Single(r.Marks).IsChip));
        Assert.All(marked.SkipLast(1), r => Assert.False(r.Marks[0].EndsHere));
        Assert.True(rows[^1].Marks.Single().EndsHere);
        Assert.Equal(ValueHintKind.Url, rows[^1].Marks[0].Hint.Kind);
    }

    /// <summary>A wrapped value with more on its line after it is marked in place on every row.</summary>
    [Fact]
    public void AWrappedValueWithMoreAfterIt_IsMarkedInPlace()
    {
        string url = "https://example.com/" + new string('a', 40);
        var rows = Mark($"[\"{url}\", 1]", width: 16);

        Assert.All(rows.SelectMany(r => r.Marks), m => Assert.False(m.IsChip));
        Assert.Equal(url, string.Concat(rows.SelectMany(r => r.Marked)));
    }

    /// <summary>A value closing exactly at a row's end hands nothing on, so a value the next row
    /// starts with is its own.</summary>
    [Fact]
    public void AValueStartingARow_AfterOneEndingTheRowAbove_IsItsOwn()
    {
        // Row one is {"a":"#336699", (15 bytes); row two starts the next value at its first byte.
        var rows = Mark("{\"a\":\"#336699\",\"https://example.com\"]", width: 15);

        Assert.Equal(["#336699"], rows[0].Marked);
        Assert.Equal("https://example.com", string.Concat(rows.Skip(1).SelectMany(r => r.Marked)));
    }

    [Fact]
    public void AStringThatDoesNotCloseOnItsLine_IsNotHinted()
        => Assert.All(Mark("{\"u\": \"https://example.com\n}"), r => Assert.Empty(r.Marks));

    [Fact]
    public void ConfigPairs_HintTheirValues_BareOrQuoted()
    {
        var rows = Mark($"TOKEN={Jwt}\ncolour: \"#336699\"  # brand\nplain = words here", lexer: new ConfigRawLexer());

        Assert.Equal(ValueHintKind.Jwt, rows[0].Marks.Single().Hint.Kind);
        Assert.Equal(Jwt, rows[0].Marked.Single());
        Assert.True(rows[0].Marks[0].IsChip);
        Assert.Equal(["#336699"], rows[1].Marked);
        Assert.Empty(rows[2].Marks);
    }

    [Fact]
    public void ABareValue_EndsBeforeItsComment_AndIsStillItsLinesChip()
    {
        var rows = Mark("link = https://example.com/a # where", lexer: new ConfigRawLexer());

        Assert.Equal(["https://example.com/a"], rows[0].Marked);
        Assert.True(rows[0].Marks[0].IsChip);
    }

    /// <summary>The lexer colours "0 2 * * *" as a number and four words; the value is all of it.</summary>
    [Fact]
    public void ABareValueOfSeveralWords_IsOneValue()
    {
        var rows = Mark("nightly = 0 2 * * *\nport: 8080", lexer: new ConfigRawLexer());

        Assert.Equal(ValueHintKind.Cron, Assert.Single(rows[0].Marks).Hint.Kind);
        Assert.Equal(["0 2 * * *"], rows[0].Marked);
        Assert.Empty(rows[1].Marks);
    }

    [Fact]
    public void ANumberInJson_EndsAtItsSeparator()
    {
        var dates = new DateHintSettings();
        var rows = Mark("[1709305509, 1709305510 ]", dates: dates);

        Assert.Equal(["1709305509", "1709305510"], rows[0].Marked);
    }

    [Fact]
    public void TheFirstDateShapedNumber_SetsTheDocumentsScheme()
    {
        var dates = new DateHintSettings();
        var rows = Mark("{\n  \"ts\": 1709305509\n}", dates: dates);

        Assert.Equal(DateDecodingScheme.JsSeconds, dates.FileDefaultScheme);
        Assert.Equal(ValueHintKind.Date, rows[1].Marks.Single().Hint.Kind);
    }

    [Fact]
    public void AChosenScheme_IsNotOverriddenByInference()
    {
        var dates = new DateHintSettings();
        dates.SetUserDefault(DateDecodingScheme.Off);
        var rows = Mark("{\n  \"ts\": 1709305509\n}", dates: dates);

        Assert.Equal(DateDecodingScheme.Off, dates.FileDefaultScheme);
        Assert.Empty(rows[1].Marks);
    }

    // ── Words, where no lexer reads values ─────────────────────────────────────────────

    [Fact]
    public void Prose_HintsUnambiguousWords_LessThePunctuationAroundThem()
    {
        var rows = Mark($"see https://example.com/a. or (#1E90FF), with {Jwt} sent 2026-10-08T09:12:03Z.", noLexer: true);

        Assert.Equal(["https://example.com/a", "#1E90FF", Jwt, "2026-10-08T09:12:03Z"], rows[0].Marked);
        Assert.All(rows[0].Marks, m => Assert.False(m.IsChip));
    }

    /// <summary>Numbers, short colours, words and Base64 that decodes to text could all be
    /// something; in prose they are left alone.</summary>
    [Fact]
    public void Prose_LeavesAnythingAmbiguousAlone()
        => Assert.Empty(Mark("sent 1709305509 bytes in #fff red Zm9vYmFyYmF6cXV4cXV1eA== at 0 9 * * 1-5 to javascript:alert(1)", noLexer: true)[0].Marks);

    [Fact]
    public void Prose_HintsBase64ThatDecodesToAKnownFormat()
    {
        // The first bytes of a PNG, with the rest of a plausible header.
        string png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 32, 0, 0, 0, 32, 8, 6, 0, 0, 0]);
        var mark = Assert.Single(Mark($"logo {png} here", noLexer: true)[0].Marks);

        Assert.Equal(ValueHintKind.Base64, mark.Hint.Kind);
    }

    [Fact]
    public void AWordWrappedOverRows_IsReadWhole()
    {
        string url = "https://example.com/" + new string('a', 40);
        var rows = Mark($"go to {url} now", width: 16, noLexer: true);

        Assert.Equal(url, string.Concat(rows.SelectMany(r => r.Marked)));
        Assert.All(rows.SelectMany(r => r.Marks), m => Assert.Equal(ValueHintKind.Url, m.Hint.Kind));
    }

    [Fact]
    public void CommentsInLexedFiles_AreReadAsWords()
    {
        var json = Mark("{\"a\": 1} // docs at https://example.com/a")[0];
        var config = Mark("port = 80 # see https://example.com/b", lexer: new ConfigRawLexer())[0];

        Assert.Equal(["https://example.com/a"], json.Marked);
        Assert.Equal(["https://example.com/b"], config.Marked);
    }

    /// <summary>A log line opens with its timestamp; marking it would mark every line alike.</summary>
    [Fact]
    public void ADateOpeningALine_IsLeftAlone_ButNotOneLaterInIt()
    {
        var rows = Mark("2026-10-11 09:12:07 WARN certificate expires 2026-11-30T00:00:00Z", noLexer: true);

        Assert.Equal(["2026-11-30T00:00:00Z"], rows[0].Marked);
    }
}
