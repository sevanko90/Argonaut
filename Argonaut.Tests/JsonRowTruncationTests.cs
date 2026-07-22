using System.Text;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Verifies the per-row display cap (JsonVisibleRowCollection.MaxDisplayTextLength): a
/// pathologically large scalar value or property name is decoded only up to the cap
/// (cut on a UTF-8 character boundary, with a trailing ellipsis) and the row carries a
/// truncation hint with the token's real length, so a single huge token can never stall
/// text layout or re-decode in full on every rebuild.
/// </summary>
public class JsonRowTruncationTests
{
    private static (JsonStructureIndex Index, MMapFile Mmap, string Path) BuildIndex(string json)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var mmap = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(mmap);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, mmap, path);
    }

    private static int FindTokenIndex(JsonStructureIndex index, System.Func<JsonTokenInfo, bool> predicate)
    {
        for (int i = 0; i < index.TokenCount; i++)
        {
            if (predicate(index.GetToken(i)))
                return i;
        }

        throw new System.InvalidOperationException("Token not found.");
    }

    private static JsonRow GetRow(JsonVisibleRowCollection rows, int tokenIndex)
    {
        int position = rows.FindVisiblePosition(tokenIndex)
            ?? throw new System.InvalidOperationException("Token not visible.");
        return (JsonRow)rows[position]!;
    }

    [Fact]
    public void LongStringValue_IsCappedWithEllipsisAndHint()
    {
        string payload = new string('a', 5000);
        var (index, mmap, path) = BuildIndex($"{{\"payload\":\"{payload}\"}}");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            var row = GetRow(rows, FindTokenIndex(index, t => t.Kind == JsonTokenKind.String));

            // Opening quote + capped text + ellipsis; no closing quote on a truncated string.
            Assert.Equal(JsonVisibleRowCollection.MaxDisplayTextLength + 2, row.Value.Length);
            Assert.StartsWith("\"", row.Value);
            Assert.EndsWith("…", row.Value);

            Assert.NotNull(row.TruncationHint);
            Assert.Contains("truncated", row.TruncationHint);
            Assert.Contains("4.9 KB", row.TruncationHint);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ShortValue_IsUnchangedWithNoHint()
    {
        var (index, mmap, path) = BuildIndex("{\"payload\":\"short\"}");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            var row = GetRow(rows, FindTokenIndex(index, t => t.Kind == JsonTokenKind.String));

            Assert.Equal("\"short\"", row.Value);
            Assert.Null(row.TruncationHint);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CapMidMultiByteCharacter_BacksOffToCharacterBoundary()
    {
        // 'a' then 1000 two-byte 'é's puts every 'é' at an odd byte offset, so the cap
        // (an even offset) falls mid-character and must back off rather than decode a
        // split sequence into a replacement glyph.
        string payload = "a" + new string('é', 1000);
        var (index, mmap, path) = BuildIndex($"{{\"payload\":\"{payload}\"}}");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            var row = GetRow(rows, FindTokenIndex(index, t => t.Kind == JsonTokenKind.String));

            Assert.EndsWith("…", row.Value);
            Assert.DoesNotContain('�', row.Value);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void LongPropertyName_IsCappedWithEllipsisAndHint()
    {
        string name = new string('k', 4000);
        var (index, mmap, path) = BuildIndex($"{{\"{name}\":1}}");
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            var row = GetRow(rows, FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number));

            Assert.NotNull(row.Name);
            Assert.Equal(JsonVisibleRowCollection.MaxDisplayTextLength + 1, row.Name!.Length);
            Assert.EndsWith("…", row.Name);

            Assert.NotNull(row.TruncationHint);
            Assert.Contains("name truncated", row.TruncationHint);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void LongNumber_IsCappedWithEllipsisAndHint()
    {
        var sb = new StringBuilder("{\"n\":1");
        sb.Append('2', 3000);
        sb.Append('}');
        var (index, mmap, path) = BuildIndex(sb.ToString());
        try
        {
            var rows = new JsonVisibleRowCollection(index, mmap);
            var row = GetRow(rows, FindTokenIndex(index, t => t.Kind == JsonTokenKind.Number));

            Assert.Equal(JsonVisibleRowCollection.MaxDisplayTextLength + 1, row.Value.Length);
            Assert.EndsWith("…", row.Value);
            Assert.NotNull(row.TruncationHint);
        }
        finally
        {
            mmap.Dispose();
            File.Delete(path);
        }
    }
}
