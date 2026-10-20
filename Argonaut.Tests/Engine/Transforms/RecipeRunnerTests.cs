using System.Buffers;
using System.Text;
using Argonaut.Engine.Bytes;
using Argonaut.Engine.Transforms;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Transforms;

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

    private static string Run(Recipe recipe, string input, TransformDirection direction = TransformDirection.Forward) =>
        Encoding.UTF8.GetString(RunBytes(recipe, new MemoryByteSource(Encoding.UTF8.GetBytes(input)), direction));

    private static byte[] RunBytes(Recipe recipe, IByteSource input, TransformDirection direction = TransformDirection.Forward)
    {
        var output = new ArrayBufferWriter<byte>();
        RecipeRunner.Run(recipe, direction, input, output, CancellationToken.None);
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

        Assert.Equal(lines.TrimEnd('\n'), Run(SqlInList, wrapped, TransformDirection.Reverse));
    }

    [Fact]
    public void Reverse_ReadsSeveralItemsOnOneLine_AndHandTypedSpacing()
    {
        string result = Run(SqlInList, "( 'a' , 'b','it''s' )", TransformDirection.Reverse);

        Assert.Equal("a\nb\nit's", result);
    }

    [Fact]
    public void Reverse_ReadsTheOriginalOnePerLineStyle()
    {
        // What the user started with: each line quoted and comma'd by hand, no wrapper.
        Assert.Equal("a\nb\n", Run(SqlInList, "'a',\n'b'\n", TransformDirection.Reverse));
    }

    [Fact]
    public void JsonArray_Reversed_UnescapesTheStrings()
    {
        string lines = "say \"hi\"\nc:\\temp\ttab\nend\\";

        Assert.Equal(lines, Run(JsonArray, Run(JsonArray, lines), TransformDirection.Reverse));
    }

    [Fact]
    public void CommaList_Reversed_SplitsAndTrims()
    {
        Assert.Equal("a\nb\nc", Run(CommaList, "a, b ,c", TransformDirection.Reverse));
    }

    [Fact]
    public void Reverse_KeepsCrLfLineEndings()
    {
        Assert.Equal("a\r\nb\r\n", Run(CommaList, "a, b\r\nc\r\n", TransformDirection.Reverse)[..6]);
    }

    [Fact]
    public void Reverse_ReportsTheLine_WhenAnItemIsNotQuoted()
    {
        var thrown = Assert.Throws<TransformException>(() => Run(SqlInList, "'a',\nb,\n'c'", TransformDirection.Reverse));

        Assert.StartsWith("Line 2:", thrown.Message);
    }

    [Fact]
    public void Reverse_RefusesATemplateWithoutExactlyOnePlaceholder()
    {
        Assert.Throws<TransformException>(() => Run(new Recipe { Template = "{line}{line}" }, "aa", TransformDirection.Reverse));
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
        foreach (var recipe in new[] { SqlInList, JsonArray, CommaList })
        {
            byte[] whole = RunBytes(recipe, new MemoryByteSource(input));
            byte[] split = RunBytes(recipe, new SplitByteSource(input, pieceSize));
            Assert.Equal(whole, split);

            byte[] back = RunBytes(recipe, new SplitByteSource(whole, pieceSize), TransformDirection.Reverse);
            Assert.Equal(input[..^1], back);
        }
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
        var input = new MemoryByteSource(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a\n", 5000))));

        Assert.ThrowsAny<OperationCanceledException>(() => RecipeRunner.Run(
            CommaList, TransformDirection.Forward, input, new ArrayBufferWriter<byte>(), stopping.Token));
    }
}
