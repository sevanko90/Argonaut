using System.Text;
using Argonaut.Engine.Detection;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Engine.Detection;

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
            return LoadFromPath.DetectFileType(path);
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

    [Theory]
    [InlineData("[\n  {\"a\": 1}\n]\n")]
    [InlineData("[\"a\", \"b\"]\n")]
    [InlineData("[ -1, 2 ]\n")]
    [InlineData("[]\n")]
    [InlineData("[true, false]\n")]
    [InlineData("[null]\n")]
    [InlineData("[\n\n  [1]\n]")]
    public void AnArrayOfAnyValue_IsJson(string content) => Assert.Equal(FileTypeDetector.FileKind.Json, Detect(content));

    /// <summary>An INI or TOML section header opens with a bracket too, but a name follows it.</summary>
    [Theory]
    [InlineData("[service]\nname = orders\n")]
    [InlineData("[nullable]\nenabled = true\n")]
    [InlineData("[truth]\nx = 1\n")]
    [InlineData("[ section ]\nkey=value\n")]
    public void ASectionHeader_IsNotJson(string content) => Assert.Equal(FileTypeDetector.FileKind.Unidentified, Detect(content));

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

    private static bool IsPlausible(FileTypeDetector.FileKind kind, string content, out string reason)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            return LoadFromPath.IsPlausibleFor(kind, path, out reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsPlausibleFor_CsvContent_RejectedForJson()
    {
        Assert.False(IsPlausible(FileTypeDetector.FileKind.Json, "a,b,c\n1,2,3\n", out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsPlausibleFor_JsonContent_RejectedForCsv()
    {
        Assert.False(IsPlausible(FileTypeDetector.FileKind.Csv, """{"a":1}""", out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsPlausibleFor_JsonContent_AcceptedForJsonAndNdjson()
    {
        Assert.True(IsPlausible(FileTypeDetector.FileKind.Json, """{"a":1}""", out _));
        Assert.True(IsPlausible(FileTypeDetector.FileKind.Ndjson, """{"a":1}""", out _));
    }

    [Fact]
    public void IsPlausibleFor_CsvContent_AcceptedForCsv()
        => Assert.True(IsPlausible(FileTypeDetector.FileKind.Csv, "a,b,c\n1,2,3\n", out _));

    [Fact]
    public void IsPlausibleFor_TsvContent_RejectedForCsv_AcceptedForTsv()
    {
        Assert.False(IsPlausible(FileTypeDetector.FileKind.Csv, "a\tb\tc\n1\t2\t3\n", out _));
        Assert.True(IsPlausible(FileTypeDetector.FileKind.Tsv, "a\tb\tc\n1\t2\t3\n", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world\n")]
    [InlineData("""{"a":1}""")]
    [InlineData("a,b,c\n1,2,3\n")]
    public void IsPlausibleFor_Unidentified_AlwaysAccepted(string content)
        => Assert.True(IsPlausible(FileTypeDetector.FileKind.Unidentified, content, out _));

    private static FileTypeDetector.FileKind DetectBytes(byte[] content)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, content);
            return LoadFromPath.DetectFileType(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root/>\n")]
    [InlineData("<?xml version='1.0'?><a><b>1</b></a>")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?>\n<root/>")]
    [InlineData("  \r\n\t<?xml version=\"1.0\"?>\n<root/>")]
    [InlineData("<root><child attr=\"1\"/></root>\n")]
    [InlineData("<core:CityModel xmlns:core=\"http://www.opengis.net/citygml/2.0\"/>")]
    [InlineData("<_private/>")]
    [InlineData("<!-- generated --><root/>")]
    [InlineData("<!DOCTYPE note SYSTEM \"note.dtd\">\n<note/>")]
    [InlineData("<?xml-stylesheet type=\"text/xsl\" href=\"a.xsl\"?>\n<root/>")]
    [InlineData("<ñame>é</ñame>")]
    [InlineData("<?xml version=\"1.0\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\"><body/></html>")]
    public void MarkupOpeningADocument_IsXml(string content) => Assert.Equal(FileTypeDetector.FileKind.Xml, Detect(content));

    /// <summary>HTML opens with markup too. Without an XML declaration, a DOCTYPE or first element
    /// naming html keeps it out of the XML view.</summary>
    [Theory]
    [InlineData("<!DOCTYPE html>\n<html><body></body></html>")]
    [InlineData("<!doctype html>\n<html>")]
    [InlineData("<!DocType HTML PUBLIC \"-//W3C//DTD HTML 4.01//EN\">\n<HTML>")]
    [InlineData("<html><head></head></html>")]
    [InlineData("<HTML lang=\"en\">")]
    [InlineData("<!-- page --><html>")]
    [InlineData("\uFEFF<html>")]
    public void Html_IsNotXml(string content) => Assert.NotEqual(FileTypeDetector.FileKind.Xml, Detect(content));

    /// <summary>A lone angle bracket, or one followed by something no XML document starts with, is
    /// text.</summary>
    [Theory]
    [InlineData("<")]
    [InlineData("< root>")]
    [InlineData("<1abc>")]
    [InlineData("<-- not a comment -->")]
    [InlineData("<= less or equal\n")]
    public void AnAngleBracketNotOpeningMarkup_IsNotXml(string content) => Assert.NotEqual(FileTypeDetector.FileKind.Xml, Detect(content));

    [Fact]
    public void Utf16_IsNotXml()
    {
        Assert.NotEqual(FileTypeDetector.FileKind.Xml, DetectBytes([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<?xml version=\"1.0\"?><a/>")]));
        Assert.NotEqual(FileTypeDetector.FileKind.Xml, DetectBytes([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("<?xml version=\"1.0\"?><a/>")]));
    }

    [Fact]
    public void AnElementNameSplitPastTheDetectionPrefix_StillDecides()
    {
        // A 70 KB comment before the root: the first element is past the prefix detection reads,
        // so it decides on what it has - markup that is not HTML.
        string content = "<!--" + new string('x', 70 * 1024) + "-->\n<root/>";
        Assert.Equal(FileTypeDetector.FileKind.Xml, Detect(content));
    }

    [Fact]
    public void XmlDetection_LeavesJsonAndDelimitedAlone()
    {
        Assert.Equal(FileTypeDetector.FileKind.Json, Detect("""{"a":"<b>"}"""));
        Assert.Equal(FileTypeDetector.FileKind.Ndjson, Detect("{\"a\":\"<x/>\"}\n{\"a\":2}\n"));
        Assert.Equal(FileTypeDetector.FileKind.Csv, Detect("a,<b>,c\n1,2,3\n"));
    }

    [Theory]
    [InlineData("<root/>")]
    [InlineData("\uFEFF  <?xml version=\"1.0\"?><a/>")]
    [InlineData("<html></html>")]
    public void IsPlausibleFor_MarkupContent_AcceptedForXml(string content)
        => Assert.True(IsPlausible(FileTypeDetector.FileKind.Xml, content, out _));

    [Theory]
    [InlineData("""{"a":1}""")]
    [InlineData("a,b,c\n1,2,3\n")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsPlausibleFor_NonMarkupContent_RejectedForXml(string content)
    {
        Assert.False(IsPlausible(FileTypeDetector.FileKind.Xml, content, out var reason));
        Assert.NotEmpty(reason);
    }
}
