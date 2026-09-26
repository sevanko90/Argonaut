using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

public class JsonStructuralScannerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TrySkipValue_MatchesUtf8JsonReaderOnEveryElement(int seed)
    {
        byte[] json = RandomDocument(new Random(seed), elements: 400);

        foreach (var source in new IByteSource[] { new MemoryByteSource(json), new SplitByteSource(json, 37), new SplitByteSource(json, 64) })
        {
            foreach (var (start, expectedEnd) in ElementSpans(json))
            {
                var outcome = JsonStructuralScanner.TrySkipValue(source, start, out long end);

                Assert.Equal(JsonSkipOutcome.Skipped, outcome);
                Assert.Equal(expectedEnd, end);
            }
        }
    }

    [Fact]
    public void TrySkipValue_MatchesUtf8JsonReaderOnEveryValueOfTheUnicodeFixture()
    {
        byte[] json = File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson);
        var values = new List<(long Start, long End)>();
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType is JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray)
                continue;

            var skipping = reader;
            skipping.Skip();
            values.Add((reader.TokenStartIndex, skipping.BytesConsumed));
        }

        Assert.True(values.Count > 30);
        foreach (var source in new IByteSource[] { new MemoryByteSource(json), new SplitByteSource(json, 7) })
        {
            foreach (var (start, expectedEnd) in values)
            {
                Assert.Equal(JsonSkipOutcome.Skipped, JsonStructuralScanner.TrySkipValue(source, start, out long end));
                Assert.Equal(expectedEnd, end);
            }
        }
    }

    [Fact]
    public void TrySkipValue_TracksBackslashRunsAcrossEveryBlockBoundary()
    {
        // Runs of one to six backslashes before a quote, slid through every alignment so each
        // run straddles a 64-byte boundary at some offset: an odd run escapes the quote, an even
        // run does not.
        for (int run = 1; run <= 6; run++)
        {
            for (int pad = 0; pad < 70; pad++)
            {
                string backslashes = new('\\', run);
                string inner = run % 2 == 1 ? backslashes + "\"x" : backslashes;
                string text = new string(' ', pad) + "[\"" + inner + "\",{\"k\":\"]\"}]";
                byte[] json = Encoding.UTF8.GetBytes(text);

                var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

                Assert.Equal(JsonSkipOutcome.Skipped, outcome);
                Assert.Equal(json.Length, end);
            }
        }
    }

    [Fact]
    public void TrySkipValue_IgnoresBracketsAndQuotesInsideStrings()
    {
        byte[] json = """{"a":"}]}]","b":["[[","\"{"],"c":"\\"}   ,1"""u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal(json.Length - "   ,1".Length, end);
    }

    [Fact]
    public void TrySkipValue_SkipsLeadingWhitespaceAndStopsBareScalarsAtTheirDelimiter()
    {
        byte[] json = "  \n\t-12.5e3 ,true]"u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal("  \n\t-12.5e3".Length, end);
    }

    [Fact]
    public void TrySkipValue_BareScalarEndsWithASettledDocument()
    {
        byte[] json = "12345"u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal(json.Length, end);
    }

    [Fact]
    public void TrySkipValue_HandsOverAtACommentInsideTheValue()
    {
        byte[] json = "[1, /* ] */ 2]"u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out _);

        Assert.Equal(JsonSkipOutcome.NeedsFullParse, outcome);
    }

    [Fact]
    public void TrySkipValue_ACommentAfterTheValueDoesNotMatter()
    {
        byte[] json = "[1, 2] // done"u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal(6, end);
    }

    [Fact]
    public void TrySkipValue_ASlashInsideAStringIsNotAComment()
    {
        byte[] json = """["http://x/y", "/*"]"""u8.ToArray();

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal(json.Length, end);
    }

    [Theory]
    [InlineData("]")]
    [InlineData(",1")]
    [InlineData("/* c */ 1")]
    [InlineData("[1, [2]")]
    [InlineData("\"open")]
    [InlineData("   ")]
    public void TrySkipValue_NeedsAFullParseWhereItCannotDecide(string text)
    {
        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(Encoding.UTF8.GetBytes(text)), 0, out _);

        Assert.Equal(JsonSkipOutcome.NeedsFullParse, outcome);
    }

    [Fact]
    public void TrySkipValue_IsIncompleteUntilTheValueHasArrived()
    {
        byte[] json = Encoding.UTF8.GetBytes("[" + string.Join(",", Enumerable.Range(0, 200)) + "]");
        var source = new GrowingByteSource(json, initiallyAvailable: json.Length / 2);

        Assert.Equal(JsonSkipOutcome.Incomplete, JsonStructuralScanner.TrySkipValue(source, 0, out _));

        source.Reveal(json.Length);
        source.Seal();

        Assert.Equal(JsonSkipOutcome.Skipped, JsonStructuralScanner.TrySkipValue(source, 0, out long end));
        Assert.Equal(json.Length, end);
    }

    [Fact]
    public void Escaped_OddRunsEscapeTheNextByteAndCarryAcrossTheBlock()
    {
        bool escapesNext = false;

        // Backslashes at 0, 2-3 and 63: 0 escapes 1, 2 escapes the backslash at 3 (so 3 escapes
        // nothing, and byte 4 is not escaped), and 63 carries into the next block.
        ulong escaped = JsonStructuralScanner.Escaped(0b1101UL | (1UL << 63), ref escapesNext);

        Assert.Equal(0b1010UL, escaped);
        Assert.True(escapesNext);

        // The carried escape consumes this block's leading backslash, so it escapes nothing.
        escaped = JsonStructuralScanner.Escaped(0b1UL, ref escapesNext);

        Assert.Equal(0b1UL, escaped);
        Assert.False(escapesNext);
    }

    /// <summary>Each root-array element's start and the end <c>Utf8JsonReader</c> reports.</summary>
    private static List<(long Start, long End)> ElementSpans(byte[] json)
    {
        var spans = new List<(long, long)>();
        var reader = new Utf8JsonReader(json);
        reader.Read(); // the root array
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            long start = reader.TokenStartIndex;
            reader.Skip();
            spans.Add((start, reader.BytesConsumed));
        }

        return spans;
    }

    private static byte[] RandomDocument(Random random, int elements)
    {
        var text = new StringBuilder("[");
        for (int i = 0; i < elements; i++)
        {
            if (i > 0)
                text.Append(',');
            text.Append(Whitespace(random));
            AppendValue(text, random, depth: 0);
            text.Append(Whitespace(random));
        }

        return Encoding.UTF8.GetBytes(text.Append(']').ToString());
    }

    private static void AppendValue(StringBuilder text, Random random, int depth)
    {
        int pick = random.Next(depth > 6 ? 5 : 7);
        switch (pick)
        {
            case 0:
                text.Append(random.Next(-100000, 100000));
                break;
            case 1:
                text.Append(random.Next(2) == 0 ? "true" : "false");
                break;
            case 2:
                text.Append("null");
                break;
            case 3:
            case 4:
                AppendString(text, random);
                break;
            case 5:
                text.Append('[');
                for (int i = 0, n = random.Next(8); i < n; i++)
                {
                    if (i > 0)
                        text.Append(',');
                    text.Append(Whitespace(random));
                    AppendValue(text, random, depth + 1);
                }
                text.Append(']');
                break;
            default:
                text.Append('{');
                for (int i = 0, n = random.Next(8); i < n; i++)
                {
                    if (i > 0)
                        text.Append(',');
                    AppendString(text, random);
                    text.Append(':').Append(Whitespace(random));
                    AppendValue(text, random, depth + 1);
                }
                text.Append('}');
                break;
        }
    }

    private static readonly string[] StringPieces =
    {
        "a", "key", "{", "}", "[", "]", ",", ":", "/", "//", "/*", "\\\"", "\\\\", "\\\\\\\"", "\\n", "\\u00e9", "é", "日本", "😀", " ",
    };

    private static void AppendString(StringBuilder text, Random random)
    {
        text.Append('"');
        for (int i = 0, n = random.Next(30); i < n; i++)
            text.Append(StringPieces[random.Next(StringPieces.Length)]);
        text.Append('"');
    }

    private static string Whitespace(Random random) => random.Next(4) switch
    {
        0 => "",
        1 => " ",
        2 => "\n  ",
        _ => new string(' ', random.Next(70)),
    };
}
