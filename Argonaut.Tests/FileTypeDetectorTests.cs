using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the structural JSON/NDJSON/CSV/TSV detection rules: first non-whitespace byte must
/// open a container; NDJSON means the first line ends with '}' and the next non-empty line
/// starts with '{'. Detection is byte-scanning only - malformed JSON bodies still count as Json
/// if the shape matches. CSV/TSV means the file isn't JSON/NDJSON and the first two physical
/// lines have an equal, non-zero count of unquoted commas (or, failing that, unquoted tabs).
/// </summary>
public class FileTypeDetectorTests
{
    private static FileTypeDetector.FileKind Detect(string content)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            return FileTypeDetector.DetectFileType(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmptyFile_IsUnidentified() => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect(""));

    [Fact]
    public void WhitespaceOnly_IsUnidentified() => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect(" \t\r\n \n"));

    [Fact]
    public void PlainText_IsUnidentified() => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("hello world\n"));

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("[1,2,3]")]
    [InlineData("  \n\t {\"a\":1}")]
    public void SingleLineContainer_IsJson(string content) => Assert.Equal(FileTypeDetector.FileKind.Json, Detect(content));

    [Fact]
    public void MultiLinePrettyPrintedObject_IsJson()
        => Assert.Equal(FileTypeDetector.FileKind.Json, Detect("{\n  \"a\": 1,\n  \"b\": 2\n}\n"));

    [Fact]
    public void ObjectPerLine_IsNdjson()
        => Assert.Equal(FileTypeDetector.FileKind.Ndjson, Detect("{\"a\":1}\n{\"b\":2}\n{\"c\":3}\n"));

    [Fact]
    public void ObjectPerLine_WithoutTrailingNewline_IsNdjson()
        => Assert.Equal(FileTypeDetector.FileKind.Ndjson, Detect("{\"a\":1}\n{\"b\":2}"));

    [Fact]
    public void ObjectPerLine_WithTrailingWhitespaceOnFirstLine_IsNdjson()
        => Assert.Equal(FileTypeDetector.FileKind.Ndjson, Detect("{\"a\":1}  \t\n{\"b\":2}\n"));

    [Fact]
    public void ObjectPerLine_WithBlankLineBetween_IsNdjson()
        => Assert.Equal(FileTypeDetector.FileKind.Ndjson, Detect("{\"a\":1}\n\n{\"b\":2}\n"));

    [Fact]
    public void ArrayFirstLine_IsJsonEvenWithObjectOnSecondLine()
        => Assert.Equal(FileTypeDetector.FileKind.Json, Detect("[\n{\"a\":1}\n]\n"));

    [Fact]
    public void SingleObjectWithTrailingNewline_IsJson()
        => Assert.Equal(FileTypeDetector.FileKind.Json, Detect("{\"a\":1}\n"));

    [Fact]
    public void MatchingCommaCounts_IsCsv()
        => Assert.Equal(FileTypeDetector.FileKind.Csv, Detect("a,b,c\n1,2,3\n"));

    [Fact]
    public void MatchingCommaCounts_WithoutTrailingNewline_IsCsv()
        => Assert.Equal(FileTypeDetector.FileKind.Csv, Detect("a,b,c\n1,2,3"));

    [Fact]
    public void MatchingTabCounts_IsTsv()
        => Assert.Equal(FileTypeDetector.FileKind.Tsv, Detect("a\tb\tc\n1\t2\t3\n"));

    [Fact]
    public void MismatchedCommaCounts_IsUnidentified()
        => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("a,b,c\n1,2\n"));

    [Fact]
    public void MismatchedTabCounts_IsUnidentified()
        => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("a\tb\tc\n1\t2\n"));

    [Fact]
    public void NoDelimiters_IsUnidentified()
        => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("hello\nworld\n"));

    [Fact]
    public void SingleLineDelimitedContent_IsUnidentified()
        => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("a,b,c"));

    [Fact]
    public void CommaInsideQuotesOnFirstLine_DoesNotCountTowardCsvMatch()
        // Line 1 has 1 real comma plus 1 quoted comma (2 raw); line 2 has 2 real commas.
        // Raw counts (2 vs 2) would wrongly match; unquoted counts (1 vs 2) correctly don't.
        => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect("\"a,b\",c\n1,2,3\n"));

    [Fact]
    public void QuotedCommaOnBothLines_StillMatchesAsCsv()
        => Assert.Equal(FileTypeDetector.FileKind.Csv, Detect("\"a,b\",c,d\n\"1,2\",3,4\n"));

    [Fact]
    public void CommaInsideQuotedTsvFieldDoesNotAffectTabDetection()
        => Assert.Equal(FileTypeDetector.FileKind.Tsv, Detect("\"a,b\"\tc\td\n\"1,2\"\t3\t4\n"));

    [Fact]
    public void JsonLikeFirstCharacter_NeverFallsThroughToDelimitedDetection()
        => Assert.Equal(FileTypeDetector.FileKind.Json, Detect("{\"a\":1,\"b\":2}\n"));
}
