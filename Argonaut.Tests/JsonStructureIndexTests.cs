using System.Text;
using System.Text.Json;
using Argonaut.Features.Json;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Round-trips a JSON document that exercises nested objects/arrays, every scalar kind,
/// empty containers, and a long property name, verifying the bit-packed
/// <see cref="JsonStructureIndex"/> storage decodes back to exactly what an independent
/// Utf8JsonReader parse of the same bytes reports.
/// </summary>
public class JsonStructureIndexTests
{
    private static string BuildSampleJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"a\":1,");
        sb.Append("\"b\":\"hello world\",");
        sb.Append("\"c\":true,");
        sb.Append("\"d\":false,");
        sb.Append("\"e\":null,");
        sb.Append("\"nested\":{\"x\":[1,2,3,{\"deep\":\"value\"}],\"y\":{}},");
        sb.Append("\"arr\":[],");
        sb.Append('"').Append(new string('n', 500)).Append("\":\"longname\"");
        sb.Append('}');
        return sb.ToString();
    }

    private static (JsonStructureIndex Index, string Json, string Path) BuildIndex()
    {
        string json = BuildSampleJson();
        string path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        var file = new MMapFile(path);
        var index = JsonStructureIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();
        return (index, json, path);
    }

    private static List<(JsonTokenKind Kind, long Offset, int Length, long NameOffset, int NameLength)> ParseExpected(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        var expected = new List<(JsonTokenKind, long, int, long, int)>();
        long pendingNameOffset = -1;
        int pendingNameLength = -1;

        while (reader.Read())
        {
            var tokenType = reader.TokenType;
            bool quoted = tokenType is JsonTokenType.String or JsonTokenType.PropertyName;
            long offset = reader.TokenStartIndex + (quoted ? 1 : 0);
            int length = reader.ValueSpan.Length;

            if (tokenType == JsonTokenType.PropertyName)
            {
                pendingNameOffset = offset;
                pendingNameLength = length;
                continue;
            }

            JsonTokenKind kind = tokenType switch
            {
                JsonTokenType.StartObject => JsonTokenKind.StartObject,
                JsonTokenType.EndObject => JsonTokenKind.EndObject,
                JsonTokenType.StartArray => JsonTokenKind.StartArray,
                JsonTokenType.EndArray => JsonTokenKind.EndArray,
                JsonTokenType.String => JsonTokenKind.String,
                JsonTokenType.Number => JsonTokenKind.Number,
                JsonTokenType.True => JsonTokenKind.True,
                JsonTokenType.False => JsonTokenKind.False,
                JsonTokenType.Null => JsonTokenKind.Null,
                _ => throw new NotSupportedException()
            };

            expected.Add((kind, offset, length, pendingNameOffset, pendingNameLength));
            pendingNameOffset = -1;
            pendingNameLength = -1;
        }

        return expected;
    }

    [Fact]
    public void TokenCount_MatchesIndependentParse()
    {
        var (index, json, path) = BuildIndex();
        try
        {
            var expected = ParseExpected(json);
            Assert.Equal(expected.Count, index.TokenCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DecodedTokens_MatchIndependentParse()
    {
        var (index, json, path) = BuildIndex();
        try
        {
            var expected = ParseExpected(json);

            for (int i = 0; i < expected.Count; i++)
            {
                var e = expected[i];
                var got = index.GetToken(i);

                Assert.Equal(e.Kind, got.Kind);
                Assert.Equal(e.Offset, got.Offset);
                Assert.Equal(e.Length, got.Length);
                Assert.Equal(e.NameOffset, got.NameOffset);
                Assert.Equal(e.NameLength, got.NameLength);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PropertyNames_DecodeToCorrectText()
    {
        var (index, json, path) = BuildIndex();
        try
        {
            bool sawLongName = false;
            for (int i = 0; i < index.TokenCount; i++)
            {
                var token = index.GetToken(i);
                if (token.NameLength < 0)
                    continue;

                string name = json.Substring((int)token.NameOffset, token.NameLength);
                if (name.Length == 500)
                {
                    Assert.Equal(new string('n', 500), name);
                    sawLongName = true;
                }
            }

            Assert.True(sawLongName, "Expected to find the 500-character property name used to stress the name-delta packing.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Containers_AllCloseWithValidEndIndex()
    {
        var (index, json, path) = BuildIndex();
        try
        {
            for (int i = 0; i < index.TokenCount; i++)
            {
                var token = index.GetToken(i);
                if (token.Kind is JsonTokenKind.StartObject or JsonTokenKind.StartArray)
                {
                    Assert.True(token.EndIndex >= 0, $"Container at token {i} never closed.");
                    Assert.True(token.EndIndex > i, $"Container at token {i} has EndIndex {token.EndIndex} that isn't after it.");
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EndToken_MatchesStartTokenDepthAndParent()
    {
        // The closing bracket must line up visually with its opening one, so it needs the
        // exact same Depth/ParentIndex as its matching Start token - not one level deeper,
        // which is what a naive stack-count-at-the-End-token read would give (the container
        // being closed is still on the stack when the End token is processed).
        var (index, json, path) = BuildIndex();
        try
        {
            for (int i = 0; i < index.TokenCount; i++)
            {
                var token = index.GetToken(i);
                if (token.Kind is not (JsonTokenKind.StartObject or JsonTokenKind.StartArray))
                    continue;

                var end = index.GetToken(token.EndIndex);
                Assert.Equal(token.Depth, end.Depth);
                Assert.Equal(token.ParentIndex, end.ParentIndex);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScalarsWithoutName_ReportNoNameSentinel()
    {
        var (index, json, path) = BuildIndex();
        try
        {
            // Array elements have no property name; find the "1,2,3" numbers inside "x".
            bool sawUnnamedScalar = false;
            for (int i = 0; i < index.TokenCount; i++)
            {
                var token = index.GetToken(i);
                if (token.Kind == JsonTokenKind.Number && token.ParentIndex >= 0)
                {
                    var parent = index.GetToken(token.ParentIndex);
                    if (parent.Kind == JsonTokenKind.StartArray)
                    {
                        Assert.Equal(-1, token.NameOffset);
                        Assert.Equal(-1, token.NameLength);
                        sawUnnamedScalar = true;
                    }
                }
            }

            Assert.True(sawUnnamedScalar, "Expected to find at least one unnamed array-element scalar.");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
