using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Indexing;

public class JsonStructuralScannerTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void TrySkipValue_MatchesUtf8JsonReaderOnEveryElement(int seed, bool jsonc)
    {
        byte[] json = RandomJson.Document(new Random(seed), elements: 400, jsonc);

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

    [Theory]
    [InlineData("[1, /* ] */ 2]")]
    [InlineData("[1, // ]\n 2]")]
    [InlineData("{\"a\": /* } \" */ [1, /*/ ] */ 2]}")]
    public void TrySkipValue_ReadsCommentsInsideTheValueAsWhitespace(string text)
    {
        byte[] json = Encoding.UTF8.GetBytes(text + " , 3");

        var outcome = JsonStructuralScanner.TrySkipValue(new MemoryByteSource(json), 0, out long end);

        Assert.Equal(JsonSkipOutcome.Skipped, outcome);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), end);
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

    /// <summary>Each root-array element's start and the end <c>Utf8JsonReader</c> reports.</summary>
    private static List<(long Start, long End)> ElementSpans(byte[] json)
    {
        var spans = new List<(long, long)>();
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        reader.Read(); // the root array
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            long start = reader.TokenStartIndex;
            reader.Skip();
            spans.Add((start, reader.BytesConsumed));
        }

        return spans;
    }
}
