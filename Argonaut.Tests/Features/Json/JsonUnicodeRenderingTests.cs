using System.Text;
using System.Text.Json;
using Argonaut.Engine.Bytes;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Indexing;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json;

/// <summary>
/// Property names and string values outside ASCII - raw multi-byte UTF-8 in many scripts, emoji
/// with ZWJ sequences, flags and skin tones, combining marks, astral-plane letters, invisible
/// characters and <c>\u</c> escapes including surrogate pairs - index and render as the text in
/// the file. A property name is any JSON string, so names get the same coverage as values.
/// </summary>
public class JsonUnicodeRenderingTests
{
    [Fact]
    public void EveryNameAndStringValue_RendersAsItsTextInTheFile()
    {
        using var file = new MMapFile(Fixtures.UnicodeNamesAndValuesJson);
        var index = JsonStructureIndex.StartIndexing(file);
        index.IndexingTask.GetAwaiter().GetResult();
        Assert.Null(index.Failure);

        var rows = new JsonVisibleRowCollection(index, file, defaultExpandDepth: 16);
        var rendered = new List<(string? Name, string Value)>();
        for (int position = 0; position < rows.Count; position++)
        {
            var row = (JsonRow)rows[position]!;
            if (row.Kind == JsonTokenKind.String)
                rendered.Add((row.Name, row.Value));
        }

        Assert.Equal(ExpectedStringRows(File.ReadAllBytes(Fixtures.UnicodeNamesAndValuesJson)), rendered);
    }

    [Fact]
    public void TheFixtureReallyContainsWhatTheTestClaims()
    {
        string text = File.ReadAllText(Fixtures.UnicodeNamesAndValuesJson, Encoding.UTF8);

        Assert.Contains("日本語", text);         // raw multi-byte name
        Assert.Contains("👨‍👩‍👧", text);          // ZWJ sequence
        Assert.Contains("𝔘𝔫𝔦𝔠𝔬𝔡𝔢", text);        // astral-plane letters in a name
        Assert.Contains(@"😀", text); // escaped surrogate pair
        Assert.Contains("\"\":", text);          // the empty name
    }

    /// <summary>Every string value in document order, with the name of the member it belongs
    /// to (null for an array element), as the row shows it: the raw bytes between the quotes,
    /// escapes kept as written.</summary>
    private static List<(string? Name, string Value)> ExpectedStringRows(byte[] json)
    {
        var expected = new List<(string?, string)>();
        var reader = new Utf8JsonReader(json);
        string? pendingName = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingName = Encoding.UTF8.GetString(reader.ValueSpan);
                    break;
                case JsonTokenType.String:
                    expected.Add((pendingName, "\"" + Encoding.UTF8.GetString(reader.ValueSpan) + "\""));
                    pendingName = null;
                    break;
                default:
                    pendingName = null;
                    break;
            }
        }

        return expected;
    }
}
