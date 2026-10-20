using System.Buffers;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Recipes;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Recipes;

public class RecipeRunnerTests
{
    private static readonly Recipe SqlInList = new()
    {
        Template = "  '{line}'",
        Escape = TextEscape.Sql,
        Separator = ",\n",
        Open = "(\n",
        Close = "\n)",
    };

    private static readonly Recipe JsonArray = new()
    {
        Template = "  \"{line}\"",
        Escape = TextEscape.Json,
        Separator = ",\n",
        Open = "[\n",
        Close = "\n]",
    };

    private static readonly Recipe CommaList = new() { Separator = ", " };

    private static Recipe Codec(RecipeCodec codec, RecipeScope scope) =>
        new() { Kind = RecipeKind.Codec, Codec = codec, Scope = scope };

    private static string Run(Recipe recipe, string input, RecipeDirection direction = RecipeDirection.Forward,
        bool inputIsPrefix = false) =>
        Encoding.UTF8.GetString(RunBytes(recipe, new MemoryByteSource(Encoding.UTF8.GetBytes(input)), direction, inputIsPrefix));

    private static byte[] RunBytes(Recipe recipe, IByteSource input, RecipeDirection direction = RecipeDirection.Forward,
        bool inputIsPrefix = false)
    {
        var output = new ArrayBufferWriter<byte>();
        RecipeRunner.Run(recipe, direction, input, output, CancellationToken.None, inputIsPrefix);
        return output.WrittenSpan.ToArray();
    }

    [Fact]
    public void SqlInList_QuotesEachLine_SeparatorOnlyBetween_AndEscapesQuotes()
    {
        string result = Run(SqlInList, "a@x.com\nb@y.com\no'brien@z.com\n");

        Assert.Equal("(\n  'a@x.com',\n  'b@y.com',\n  'o''brien@z.com'\n)", result);
    }

    [Fact]
    public void Template_TrimsAndSkipsBlankLines_ByDefault()
    {
        Assert.Equal("a, b", Run(CommaList, "  a \n\n\t\nb\r\n"));
    }

    [Fact]
    public void Template_KeepsBlankLines_WhenAskedTo()
    {
        Assert.Equal("a, , b", Run(CommaList with { SkipBlankLines = false }, "a\n\nb"));
    }

    [Fact]
    public void Template_WithSeveralPlaceholders_RepeatsTheLine()
    {
        Assert.Equal("a=a;b=b", Run(new Recipe { Template = "{line}={line}", Separator = ";" }, "a\nb"));
    }

    [Fact]
    public void JsonArray_EscapesQuotesAndBackslashes()
    {
        string result = Run(JsonArray, "say \"hi\"\nc:\\temp\ttab");

        Assert.Equal("[\n  \"say \\\"hi\\\"\",\n  \"c:\\\\temp\\ttab\"\n]", result);
    }

    [Theory]
    [InlineData("a@x.com\nb@y.com\no'brien@z.com\n")]
    [InlineData("one\ntwo")]
    [InlineData("''\n'\nit's")]
    public void SqlInList_Reversed_GivesTheLinesBack(string lines)
    {
        string wrapped = Run(SqlInList, lines);

        Assert.Equal(lines.TrimEnd('\n'), Run(SqlInList, wrapped, RecipeDirection.Reverse));
    }

    [Fact]
    public void Reverse_ReadsSeveralItemsOnOneLine_AndHandTypedSpacing()
    {
        string result = Run(SqlInList, "( 'a' , 'b','it''s' )", RecipeDirection.Reverse);

        Assert.Equal("a\nb\nit's", result);
    }

    [Fact]
    public void Reverse_ReadsTheOriginalOnePerLineStyle()
    {
        // What the user started with: each line quoted and comma'd by hand, no wrapper.
        Assert.Equal("a\nb\n", Run(SqlInList, "'a',\n'b'\n", RecipeDirection.Reverse));
    }

    [Fact]
    public void JsonArray_Reversed_UnescapesTheStrings()
    {
        string lines = "say \"hi\"\nc:\\temp\ttab\nend\\";

        Assert.Equal(lines, Run(JsonArray, Run(JsonArray, lines), RecipeDirection.Reverse));
    }

    [Fact]
    public void CommaList_Reversed_SplitsAndTrims()
    {
        Assert.Equal("a\nb\nc", Run(CommaList, "a, b ,c", RecipeDirection.Reverse));
    }

    [Fact]
    public void Reverse_KeepsCrLfLineEndings()
    {
        Assert.Equal("a\r\nb\r\n", Run(CommaList, "a, b\r\nc\r\n", RecipeDirection.Reverse)[..6]);
    }

    [Fact]
    public void Reverse_ReportsTheLine_WhenAnItemIsNotQuoted()
    {
        var thrown = Assert.Throws<RecipeException>(() => Run(SqlInList, "'a',\nb,\n'c'", RecipeDirection.Reverse));

        Assert.StartsWith("Line 2:", thrown.Message);
    }

    [Fact]
    public void Reverse_RefusesATemplateWithoutExactlyOnePlaceholder()
    {
        Assert.Throws<RecipeException>(() => Run(new Recipe { Template = "{line}{line}" }, "aa", RecipeDirection.Reverse));
    }

    [Theory]
    [InlineData(RecipeCodec.JsonString, "line 1\n\"quoted\"\t\u0001 é", "line 1\\n\\\"quoted\\\"\\t\\u0001 é")]
    [InlineData(RecipeCodec.Url, "a b/c?d=é", "a%20b%2Fc%3Fd%3D%C3%A9")]
    [InlineData(RecipeCodec.Base64, "hello?>", "aGVsbG8/Pg==")]
    [InlineData(RecipeCodec.Base64Url, "hello?>", "aGVsbG8_Pg")]
    public void Codec_WholeText_EncodesAndDecodes(RecipeCodec codec, string text, string encoded)
    {
        var recipe = Codec(codec, RecipeScope.WholeText);

        Assert.Equal(encoded, Run(recipe, text));
        Assert.Equal(text, Run(recipe, encoded, RecipeDirection.Reverse));
    }

    [Theory]
    [InlineData(RecipeCodec.Base64)]
    [InlineData(RecipeCodec.Base64Url)]
    public void Base64_EachLine_EncodesLinesSeparately_KeepingLineBreaks(RecipeCodec codec)
    {
        var recipe = Codec(codec, RecipeScope.EachLine);

        string encoded = Run(recipe, "a\nbb\n");

        Assert.Equal(codec == RecipeCodec.Base64 ? "YQ==\nYmI=\n" : "YQ\nYmI\n", encoded);
        Assert.Equal("a\nbb\n", Run(recipe, encoded, RecipeDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_IgnoresLineWrapping_AndToleratesMissingPadding()
    {
        var recipe = Codec(RecipeCodec.Base64, RecipeScope.WholeText);

        Assert.Equal("hello world", Run(recipe, "aGVsbG8g\r\nd29y\nbGQ", RecipeDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_RefusesText_ThatIsNotBase64()
    {
        Assert.Throws<RecipeException>(() =>
            Run(Codec(RecipeCodec.Base64, RecipeScope.WholeText), "not*base64", RecipeDirection.Reverse));
    }

    [Fact]
    public void Base64Decode_OfAPrefix_StopsAtTheLastWholeQuantum()
    {
        var recipe = Codec(RecipeCodec.Base64, RecipeScope.WholeText);

        Assert.Equal("hello", Run(recipe, "aGVsbG8gd2", RecipeDirection.Reverse, inputIsPrefix: true)[..5]);
    }

    [Fact]
    public void JsonStringDecode_AcceptsTheSurroundingQuotes_AndSurrogatePairs()
    {
        var recipe = Codec(RecipeCodec.JsonString, RecipeScope.EachLine);

        Assert.Equal("a\"😀", Run(recipe, "\"a\\\"\\ud83d\\ude00\"", RecipeDirection.Reverse));
    }

    [Fact]
    public void JsonStringDecode_ReportsAnUnknownEscape_WithItsLine()
    {
        var thrown = Assert.Throws<RecipeException>(() =>
            Run(Codec(RecipeCodec.JsonString, RecipeScope.EachLine), "ok\nbad \\q", RecipeDirection.Reverse));

        Assert.StartsWith("Line 2:", thrown.Message);
    }

    [Fact]
    public void UrlDecode_RefusesABrokenPercentSequence()
    {
        Assert.Throws<RecipeException>(() =>
            Run(Codec(RecipeCodec.Url, RecipeScope.EachLine), "100%", RecipeDirection.Reverse));
    }

    /// <summary>Every recipe over a source split into small runs, against the same bytes in one
    /// run: a line, an escape or a base64 quantum straddling a boundary must read the same.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    public void SplitSource_GivesTheSameResultAsOneRun(int pieceSize)
    {
        var random = new Random(pieceSize);
        var text = new StringBuilder();
        for (int i = 0; i < 200; i++)
            text.Append("line ").Append(i).Append(" 'q' \"d\" \\ é ").Append(random.Next()).Append('\n');

        byte[] input = Encoding.UTF8.GetBytes(text.ToString());
        Recipe[] recipes =
        [
            SqlInList, JsonArray, CommaList,
            Codec(RecipeCodec.Base64, RecipeScope.WholeText),
            Codec(RecipeCodec.Base64Url, RecipeScope.WholeText),
            Codec(RecipeCodec.JsonString, RecipeScope.WholeText),
            Codec(RecipeCodec.Url, RecipeScope.WholeText),
        ];

        foreach (var recipe in recipes)
        {
            byte[] whole = RunBytes(recipe, new MemoryByteSource(input));
            byte[] split = RunBytes(recipe, new SplitByteSource(input, pieceSize));
            Assert.Equal(whole, split);

            byte[] back = RunBytes(recipe, new SplitByteSource(whole, pieceSize), RecipeDirection.Reverse);
            Assert.Equal(recipe.Kind == RecipeKind.Codec ? input : input[..^1], back);
        }
    }

    [Fact]
    public void Base64_OfMoreThanOneCodecChunk_RoundTrips()
    {
        var input = new byte[200_001];
        new Random(1).NextBytes(input);
        var recipe = Codec(RecipeCodec.Base64, RecipeScope.WholeText);

        byte[] encoded = RunBytes(recipe, new MemoryByteSource(input));

        Assert.Equal(Convert.ToBase64String(input), Encoding.ASCII.GetString(encoded));
        Assert.Equal(input, RunBytes(recipe, new MemoryByteSource(encoded), RecipeDirection.Reverse));
    }

    [Fact]
    public void ALineLongerThanAScanChunk_IsReadWhole()
    {
        string longLine = new('x', 200_000);

        Assert.Equal($"  '{longLine}'", Run(SqlInList with { Open = "", Close = "" }, longLine + "\nshort")[..(longLine.Length + 4)]);
    }

    [Fact]
    public void Run_StopsWhenAsked()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var input = new MemoryByteSource(Encoding.UTF8.GetBytes(new string('a', 100_000)));

        Assert.ThrowsAny<OperationCanceledException>(() => RecipeRunner.Run(
            Codec(RecipeCodec.Base64, RecipeScope.WholeText), RecipeDirection.Forward, input,
            new ArrayBufferWriter<byte>(), stopping.Token));
    }
}
