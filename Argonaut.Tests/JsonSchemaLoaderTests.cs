using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests;

/// <summary>
/// Covers <see cref="JsonSchemaLoader"/>'s keyword handling and its "never throw, never
/// complain" failure contract: anything unusable comes back as null.
/// </summary>
public class JsonSchemaLoaderTests
{
    private static JsonSchemaDocument Parse(string json)
        => JsonSchemaLoader.TryParse(json) ?? throw new InvalidOperationException("Schema failed to load.");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private static string? MemberTitle(JsonSchemaDocument schema, int parentId, string key)
        => schema.GetTitle(schema.ResolveMember(parentId, Utf8(key)));

    [Fact]
    public void LocalRef_ResolvesThroughDefs()
    {
        var schema = Parse("""
            {
              "properties": { "a": { "$ref": "#/$defs/thing" } },
              "$defs": { "thing": { "title": "Thing" } }
            }
            """);

        Assert.Equal("Thing", MemberTitle(schema, schema.RootId, "a"));
    }

    [Fact]
    public void LocalRef_ResolvesThroughDraft07Definitions()
    {
        var schema = Parse("""
            {
              "properties": { "a": { "$ref": "#/definitions/thing" } },
              "definitions": { "thing": { "title": "Thing" } }
            }
            """);

        Assert.Equal("Thing", MemberTitle(schema, schema.RootId, "a"));
    }

    [Fact]
    public void RecursiveRef_TerminatesAndKeepsResolving()
    {
        var schema = Parse("""
            {
              "title": "Node",
              "properties": {
                "name": { "title": "Name" },
                "child": { "$ref": "#" }
              }
            }
            """);

        int child = schema.ResolveMember(schema.RootId, Utf8("child"));
        int grandchild = schema.ResolveMember(child, Utf8("child"));

        Assert.Equal("Node", schema.GetTitle(child));
        Assert.Equal("Name", MemberTitle(schema, grandchild, "name"));
    }

    [Fact]
    public void RefWithOwnTitle_KeepsItsOwnAndInheritsStructure()
    {
        var schema = Parse("""
            {
              "properties": { "a": { "$ref": "#/$defs/thing", "title": "At this spot" } },
              "$defs": { "thing": { "title": "Generic", "properties": { "inner": { "title": "Inner" } } } }
            }
            """);

        int a = schema.ResolveMember(schema.RootId, Utf8("a"));

        Assert.Equal("At this spot", schema.GetTitle(a));
        Assert.Equal("Inner", MemberTitle(schema, a, "inner"));
    }

    [Fact]
    public void MutuallyRecursiveRefs_DoNotHang()
    {
        var schema = Parse("""
            {
              "$ref": "#/$defs/a",
              "$defs": {
                "a": { "$ref": "#/$defs/b" },
                "b": { "$ref": "#/$defs/a", "title": "B" }
              }
            }
            """);

        Assert.NotNull(schema);
    }

    [Fact]
    public void AllOf_MergesTitleAndUnionsProperties()
    {
        var schema = Parse("""
            {
              "properties": { "z": { "title": "Z" } },
              "allOf": [
                { "properties": { "x": { "title": "X" } } },
                { "title": "Merged", "properties": { "y": { "title": "Y" } } }
              ]
            }
            """);

        Assert.Equal("Merged", schema.GetTitle(schema.RootId));
        Assert.Equal("X", MemberTitle(schema, schema.RootId, "x"));
        Assert.Equal("Y", MemberTitle(schema, schema.RootId, "y"));
        Assert.Equal("Z", MemberTitle(schema, schema.RootId, "z"));
    }

    [Fact]
    public void AllOf_OwnKeywordsWinOverBranches()
    {
        var schema = Parse("""
            {
              "title": "Own",
              "properties": { "x": { "title": "Own X" } },
              "allOf": [ { "title": "Branch", "properties": { "x": { "title": "Branch X" } } } ]
            }
            """);

        Assert.Equal("Own", schema.GetTitle(schema.RootId));
        Assert.Equal("Own X", MemberTitle(schema, schema.RootId, "x"));
    }

    [Fact]
    public void OneOf_OfConsts_BecomesEnumLabels_WithNoStructuralDescent()
    {
        var schema = Parse("""
            {
              "properties": {
                "status": {
                  "title": "Status",
                  "oneOf": [
                    { "const": "a", "title": "Alpha", "description": "The first one." },
                    { "const": "b", "title": "Beta" }
                  ]
                }
              }
            }
            """);

        int status = schema.ResolveMember(schema.RootId, Utf8("status"));

        Assert.True(schema.TryGetEnumLabel(status, "\"a\"", JsonTokenKind.String, out var title, out var description));
        Assert.Equal("Alpha", title);
        Assert.Equal("The first one.", description);

        // A const branch is a value, not a shape - it must not have contributed structure.
        Assert.Equal(-1, schema.ResolveMember(status, Utf8("const")));
    }

    [Fact]
    public void OneOf_OfShapes_IsMergedStructurally()
    {
        var schema = Parse("""
            {
              "oneOf": [
                { "properties": { "p": { "title": "P" } } },
                { "properties": { "q": { "title": "Q" } } }
              ]
            }
            """);

        Assert.Equal("P", MemberTitle(schema, schema.RootId, "p"));
        Assert.Equal("Q", MemberTitle(schema, schema.RootId, "q"));
    }

    [Fact]
    public void AnyOf_OfUndocumentedConsts_IsTreatedStructurally()
    {
        // No titles or descriptions on the branches means there's nothing to label with, so the
        // union must not swallow itself into an empty enum table.
        var schema = Parse("""{ "properties": { "s": { "anyOf": [ { "const": "a" }, { "const": "b" } ] } } }""");

        int s = schema.ResolveMember(schema.RootId, Utf8("s"));
        Assert.False(schema.TryGetEnumLabel(s, "\"a\"", JsonTokenKind.String, out _, out _));
    }

    [Fact]
    public void Draft07ArrayItems_BehaveAsPrefixItems()
    {
        var schema = Parse("""{ "items": [ { "title": "First" }, { "title": "Second" } ] }""");

        Assert.Equal("First", schema.GetTitle(schema.ResolveElement(schema.RootId, 0)));
        Assert.Equal("Second", schema.GetTitle(schema.ResolveElement(schema.RootId, 1)));
        Assert.Equal(-1, schema.ResolveElement(schema.RootId, 2));
    }

    [Fact]
    public void PrefixItems_WinOverArrayFormItems()
    {
        var schema = Parse("""
            {
              "prefixItems": [ { "title": "New style" } ],
              "items": [ { "title": "Old style" } ]
            }
            """);

        Assert.Equal("New style", schema.GetTitle(schema.ResolveElement(schema.RootId, 0)));
    }

    [Fact]
    public void EnumWithXEnumNames_BecomesLabels()
    {
        var schema = Parse("""
            {
              "properties": {
                "kind": {
                  "enum": [0, 1],
                  "x-enumNames": ["Standard", "Deluxe"],
                  "x-enumDescriptions": ["The plain one.", "The fancy one."]
                }
              }
            }
            """);

        int kind = schema.ResolveMember(schema.RootId, Utf8("kind"));

        Assert.True(schema.TryGetEnumLabel(kind, "1", JsonTokenKind.Number, out var title, out var description));
        Assert.Equal("Deluxe", title);
        Assert.Equal("The fancy one.", description);
    }

    [Fact]
    public void EnumWithoutLabels_ProducesNoTable()
    {
        var schema = Parse("""{ "properties": { "kind": { "enum": [0, 1] } } }""");

        int kind = schema.ResolveMember(schema.RootId, Utf8("kind"));
        Assert.False(schema.TryGetEnumLabel(kind, "1", JsonTokenKind.Number, out _, out _));
    }

    [Fact]
    public void RemoteRefAndUnknownKeywords_AreIgnoredSilently()
    {
        var schema = Parse("""
            {
              "properties": {
                "a": { "$ref": "https://example.com/other.json#/$defs/thing" },
                "b": { "title": "B" }
              },
              "patternProperties": { "^x": { "title": "Never used" } },
              "if": { "title": "Also never used" }
            }
            """);

        Assert.Null(MemberTitle(schema, schema.RootId, "a"));
        Assert.Equal("B", MemberTitle(schema, schema.RootId, "b"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("true")]        // a boolean schema documents nothing
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void UnusableSchema_ReturnsNull(string json) => Assert.Null(JsonSchemaLoader.TryParse(json));

    [Fact]
    public void MissingFile_ReturnsNull()
        => Assert.Null(JsonSchemaLoader.TryLoadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));

    [Fact]
    public void OversizedFile_ReturnsNull_WithoutParsing()
    {
        string path = Path.GetTempFileName();
        try
        {
            // Valid JSON, but past the cap - it must be rejected on size alone.
            File.WriteAllText(path, "{\"title\":\"" + new string('x', JsonSchemaLoader.MaxSchemaBytes + 16) + "\"}");
            Assert.Null(JsonSchemaLoader.TryLoadFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
