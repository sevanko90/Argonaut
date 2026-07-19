using System.Text;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the structural JSON/NDJSON detection rules: first non-whitespace byte must open
/// a container; NDJSON means the first line ends with '}' and the next non-empty line starts
/// with '{'. Detection is byte-scanning only - malformed JSON bodies still count as Json if
/// the shape matches.
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
    public void EmptyFile_IsNotJson() => Assert.Equal(FileTypeDetector.FileKind.NotJson, Detect(""));

    [Fact]
    public void WhitespaceOnly_IsNotJson() => Assert.Equal(FileTypeDetector.FileKind.NotJson, Detect(" \t\r\n \n"));

    [Fact]
    public void PlainText_IsNotJson() => Assert.Equal(FileTypeDetector.FileKind.NotJson, Detect("hello world\n"));

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
}
