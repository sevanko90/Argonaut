using System.Text;
using Argonaut.Features.Csv;
using Argonaut.Features.NdJson;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Regression tests for forcing the NDJSON or CSV view onto a minified JSON file (GH: view
/// switch hung the UI). Such a file has no newlines at all, so FileOffsetIndex indexes it as a
/// single line spanning the whole file. Every display path that materializes "a line" must stay
/// bounded on that input - decoding it whole, or splitting it into one cell per comma, froze the
/// UI thread outright.
/// </summary>
public class NewlinelessFileDisplayCapTests
{
    /// <summary>A minified-JSON-shaped payload: no newline anywhere, many commas.</summary>
    private static string MinifiedJson(int objects)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < objects; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append("{\"a\":1,\"b\":2,\"c\":3}");
        }

        return sb.Append(']').ToString();
    }

    private static void WithFile(string content, Action<MMapFile, FileOffsetIndex> assert)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            using var file = new MMapFile(path);
            var index = FileOffsetIndex.StartIndexing(file);
            index.IndexingTask.GetAwaiter().GetResult();
            assert(file, index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewlinelessFile_IndexesAsExactlyOneLine()
        => WithFile(MinifiedJson(500), (_, index) => Assert.Equal(1, index.LineCount));

    [Fact]
    public void NdJsonDisplayLine_IsCapped_NotWholeFile()
        => WithFile(MinifiedJson(5000), (file, index) =>
        {
            var span = index.GetLineSpan(0);
            Assert.True(span.Length > DisplayText.MaxLength, "payload must exceed the cap to be a useful test");

            string text = NdJsonLineReader.ReadDisplayLine(file, span);

            Assert.True(text.Length <= DisplayText.MaxLength + 1, $"line decoded to {text.Length} chars");
            Assert.EndsWith("…", text);
        });

    [Fact]
    public void NdJsonDisplayLine_ShortLineIsUnchanged()
        => WithFile("{\"a\":1}\n{\"b\":2}\n", (file, index) =>
        {
            Assert.Equal("{\"a\":1}", NdJsonLineReader.ReadDisplayLine(file, index.GetLineSpan(0)));
            Assert.Equal("{\"b\":2}", NdJsonLineReader.ReadDisplayLine(file, index.GetLineSpan(1)));
        });

    [Fact]
    public void CsvFields_AreCappedInCount_NotOnePerComma()
        => WithFile(MinifiedJson(5000), (file, index) =>
        {
            var fields = CsvFieldReader.ReadFields(file, index.GetLineSpan(0), (byte)',');

            Assert.True(fields.Length <= CsvFieldReader.MaxDisplayFields,
                $"row split into {fields.Length} fields");
        });

    [Fact]
    public void CsvFields_AreCappedInLength_SoNoCellIsHuge()
        => WithFile(MinifiedJson(5000), (file, index) =>
        {
            var fields = CsvFieldReader.ReadFields(file, index.GetLineSpan(0), (byte)',');

            Assert.All(fields, f => Assert.True(f.Length <= DisplayText.MaxLength + 1,
                $"cell decoded to {f.Length} chars"));
        });

    [Fact]
    public void CsvSearchSplit_StaysUncapped_SoMatchesInLaterColumnsAreFound()
        => WithFile(MinifiedJson(5000), (file, index) =>
        {
            // Search must still see every column - only the display path is capped.
            var spans = CsvFieldReader.SplitToSpans(file, index.GetLineSpan(0), (byte)',');

            Assert.True(spans.Length > CsvFieldReader.MaxDisplayFields,
                $"uncapped split produced only {spans.Length} fields");
        });

    [Fact]
    public void CsvPreflight_OnNewlinelessFile_StopsAtThePrefixLimit()
    {
        // No newline and no delimiter until well past the pre-flight limit. If the pre-flight
        // still scanned the whole (unbounded) "first line" byte-at-a-time on the UI thread it
        // would find that comma and report plausible; stopping at the prefix means it does not.
        string content = new string('x', 2 * 1024 * 1024) + ",a,b";

        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

            Assert.False(FileTypeDetector.IsPlausibleFor(FileTypeDetector.FileKind.Csv, path, out string reason));
            Assert.Contains("delimiter", reason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CsvPreflight_OnNormalCsv_IsStillPlausible()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, "name,age\nalice,30\n"u8.ToArray());
            Assert.True(FileTypeDetector.IsPlausibleFor(FileTypeDetector.FileKind.Csv, path, out _));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
