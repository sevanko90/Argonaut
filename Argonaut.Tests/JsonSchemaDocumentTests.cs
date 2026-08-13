using System.Text;
using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests;

/// <summary>
/// Covers the walk-time lookups in <see cref="JsonSchemaDocument"/>: the UTF-8 member binary
/// search, positional array resolution, and the textual enum matching (including its documented
/// numeric-normalisation limit).
/// </summary>
public class JsonSchemaDocumentTests
{
    private static JsonSchemaDocument Parse(string json)
        => JsonSchemaLoader.TryParse(json) ?? throw new InvalidOperationException("Schema failed to load.");

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    private const string ObjectSchema = """
        {
          "properties": {
            "zebra": { "title": "Zebra" },
            "alpha": { "title": "Alpha" },
            "mid": { "title": "Mid" },
            "ünïcode": { "title": "Non-ASCII" }
          }
        }
        """;

    [Theory]
    [InlineData("alpha", "Alpha")]
    [InlineData("mid", "Mid")]
    [InlineData("zebra", "Zebra")]
    [InlineData("ünïcode", "Non-ASCII")]
    public void ResolveMember_FindsEveryKey_RegardlessOfDeclarationOrder(string key, string expected)
    {
        var schema = Parse(ObjectSchema);
        Assert.Equal(expected, schema.GetTitle(schema.ResolveMember(schema.RootId, Utf8(key))));
    }

    [Theory]
    [InlineData("aaa")]     // sorts before every key
    [InlineData("nope")]    // sorts between keys
    [InlineData("zzz")]     // sorts after every key
    [InlineData("")]
    [InlineData("alph")]    // a prefix of a real key, which must not match it
    [InlineData("alphaa")]
    public void ResolveMember_Miss_ReturnsNoNode(string key)
    {
        var schema = Parse(ObjectSchema);
        Assert.Equal(-1, schema.ResolveMember(schema.RootId, Utf8(key)));
    }

    [Fact]
    public void ResolveMember_Miss_FallsBackToAdditionalProperties()
    {
        var schema = Parse("""
            {
              "properties": { "known": { "title": "Known" } },
              "additionalProperties": { "title": "Anything else" }
            }
            """);

        Assert.Equal("Known", schema.GetTitle(schema.ResolveMember(schema.RootId, Utf8("known"))));
        Assert.Equal("Anything else", schema.GetTitle(schema.ResolveMember(schema.RootId, Utf8("surprise"))));
    }

    [Fact]
    public void ResolveMember_OnUnknownNode_ReturnsNoNode()
    {
        var schema = Parse(ObjectSchema);
        Assert.Equal(-1, schema.ResolveMember(-1, Utf8("alpha")));
        Assert.Equal(-1, schema.ResolveMember(int.MaxValue, Utf8("alpha")));
    }

    [Fact]
    public void ResolveElement_UsesPrefixItemsThenItems()
    {
        var schema = Parse("""
            {
              "prefixItems": [ { "title": "Slot 0" }, { "title": "Slot 1" } ],
              "items": { "title": "The rest" }
            }
            """);

        Assert.Equal("Slot 0", schema.GetTitle(schema.ResolveElement(schema.RootId, 0)));
        Assert.Equal("Slot 1", schema.GetTitle(schema.ResolveElement(schema.RootId, 1)));
        Assert.Equal("The rest", schema.GetTitle(schema.ResolveElement(schema.RootId, 2)));
        Assert.Equal("The rest", schema.GetTitle(schema.ResolveElement(schema.RootId, 10_000)));
    }

    [Fact]
    public void ResolveElement_WithoutItems_RunsOutPastPrefixItems()
    {
        var schema = Parse("""{ "prefixItems": [ { "title": "Only slot" } ] }""");

        Assert.Equal("Only slot", schema.GetTitle(schema.ResolveElement(schema.RootId, 0)));
        Assert.Equal(-1, schema.ResolveElement(schema.RootId, 1));
        Assert.Equal(-1, schema.ResolveElement(schema.RootId, -1));
    }

    private const string EnumSchema = """
        {
          "properties": {
            "text": {
              "oneOf": [ { "const": "on", "title": "Switched on" }, { "const": "off", "title": "Switched off" } ]
            },
            "number": {
              "enum": [3, 4.5],
              "x-enumNames": ["Three", "Four and a half"]
            },
            "flag": {
              "oneOf": [ { "const": true, "title": "Yes" }, { "const": false, "title": "No" } ]
            }
          }
        }
        """;

    private static int Member(JsonSchemaDocument schema, string key) => schema.ResolveMember(schema.RootId, Utf8(key));

    [Fact]
    public void TryGetEnumLabel_MatchesStringValue_StrippingDisplayQuotes()
    {
        var schema = Parse(EnumSchema);

        Assert.True(schema.TryGetEnumLabel(Member(schema, "text"), "\"off\"", JsonTokenKind.String, out var title, out _));
        Assert.Equal("Switched off", title);
    }

    [Fact]
    public void TryGetEnumLabel_DoesNotMatchTruncatedString()
    {
        var schema = Parse(EnumSchema);

        // BuildScalarText drops the closing quote when a value was display-truncated.
        Assert.False(schema.TryGetEnumLabel(Member(schema, "text"), "\"off", JsonTokenKind.String, out _, out _));
    }

    [Theory]
    [InlineData("3", "Three")]
    [InlineData("3.0", "Three")]        // decimal-normalised, the documented number-matching rule
    [InlineData("3.00", "Three")]
    [InlineData("4.5", "Four and a half")]
    public void TryGetEnumLabel_MatchesNumberValue_Normalised(string valueText, string expected)
    {
        var schema = Parse(EnumSchema);

        Assert.True(schema.TryGetEnumLabel(Member(schema, "number"), valueText, JsonTokenKind.Number, out var title, out _));
        Assert.Equal(expected, title);
    }

    [Fact]
    public void TryGetEnumLabel_MatchesBooleanValue()
    {
        var schema = Parse(EnumSchema);

        Assert.True(schema.TryGetEnumLabel(Member(schema, "flag"), "true", JsonTokenKind.True, out var title, out _));
        Assert.Equal("Yes", title);
    }

    [Fact]
    public void TryGetEnumLabel_NoMatch_OrNoTable_ReturnsFalse()
    {
        var schema = Parse(EnumSchema);

        Assert.False(schema.TryGetEnumLabel(Member(schema, "number"), "99", JsonTokenKind.Number, out _, out _));
        Assert.False(schema.TryGetEnumLabel(schema.RootId, "3", JsonTokenKind.Number, out _, out _));
        Assert.False(schema.TryGetEnumLabel(-1, "3", JsonTokenKind.Number, out _, out _));
    }
}
