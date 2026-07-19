using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies CsvFieldReader's quote-aware field splitting: plain fields, quoted fields with
/// embedded delimiters, doubled-quote escaping, empty/trailing-empty fields, and the tab
/// delimiter. Uses FileOffsetIndex to get a real FileLineSpan for row 0, matching how
/// CsvViewModel obtains it.
/// </summary>
public class CsvFieldReaderTests
{
    private static void WithFirstRow(string content, byte delimiter, Action<string[]> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();

            var fields = CsvFieldReader.ReadFields(file, index.GetLineSpan(0), delimiter);
            assert(fields);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlainFields_SplitOnComma()
        => WithFirstRow("a,b,c\n", (byte)',', fields => Assert.Equal(["a", "b", "c"], fields));

    [Fact]
    public void PlainFields_SplitOnTab()
        => WithFirstRow("a\tb\tc\n", (byte)'\t', fields => Assert.Equal(["a", "b", "c"], fields));

    [Fact]
    public void QuotedFieldWithEmbeddedComma_IsOneField()
        => WithFirstRow("\"a,b\",c,d\n", (byte)',', fields => Assert.Equal(["a,b", "c", "d"], fields));

    [Fact]
    public void QuotedFieldWithDoubledQuote_Unescapes()
        => WithFirstRow("\"ab\"\"cd\",e\n", (byte)',', fields => Assert.Equal(["ab\"cd", "e"], fields));

    [Fact]
    public void EmptyQuotedField_DecodesToEmptyString()
        => WithFirstRow("\"\",b\n", (byte)',', fields => Assert.Equal(["", "b"], fields));

    [Fact]
    public void EmptyFields_BetweenAndTrailing()
        => WithFirstRow("a,,c,\n", (byte)',', fields => Assert.Equal(["a", "", "c", ""], fields));

    [Fact]
    public void LeadingEmptyField()
        => WithFirstRow(",b,c\n", (byte)',', fields => Assert.Equal(["", "b", "c"], fields));

    [Fact]
    public void NoTrailingNewline_StillSplitsCorrectly()
        => WithFirstRow("a,b,c", (byte)',', fields => Assert.Equal(["a", "b", "c"], fields));

    [Fact]
    public void SingleFieldNoDelimiter_ReturnsOneField()
        => WithFirstRow("onlyfield\n", (byte)',', fields => Assert.Equal(["onlyfield"], fields));

    [Fact]
    public void EmptyLine_ReturnsSingleEmptyField()
        => WithFirstRow("\n", (byte)',', fields => Assert.Equal([""], fields));

    [Fact]
    public void UnwrappedQuoteInMiddleOfField_LeftAsIs()
        => WithFirstRow("ab\"cd\"ef,g\n", (byte)',', fields => Assert.Equal(["ab\"cd\"ef", "g"], fields));

    [Fact]
    public void CrlfLineEnding_TrimmedFromLastField()
        => WithFirstRow("a,b,c\r\n", (byte)',', fields => Assert.Equal(["a", "b", "c"], fields));
}
