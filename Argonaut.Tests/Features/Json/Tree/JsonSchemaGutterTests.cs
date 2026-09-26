using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Tests.Support;

namespace Argonaut.Tests.Features.Json.Tree;

/// <summary>
/// What the schema gutter says for each row of a bound document: titles, descriptions and the
/// label drawn from them, enum member labels, positional array slots, and silence where the
/// schema runs out or is unbound. Then the same labels over random documents at any depth.
/// </summary>
public class JsonSchemaGutterTests
{
    private const string Json = """
        {"name":"widget","status":"a","ts":1709305509,"csv":[[1,2],[3,4]],"nested":{"deep":{"deeper":1}},"bare":"x","prose":"y","markup":"z"}
        """;

    private const string Schema = """
        {
          "properties": {
            "name": { "title": "Product name", "description": "What the thing is called." },
            "status": {
              "title": "Status",
              "oneOf": [ { "const": "a", "title": "Active", "description": "Currently sold." } ]
            },
            "ts": { "title": "Created" },
            "csv": {
              "title": "Series",
              "prefixItems": [ { "title": "First series" }, { "title": "Second series" } ]
            },
            "nested": { "title": "Nested" },
            "bare": { "description": "Only a description, no title." },
            "prose": { "description": "First line only.\n\nSecond paragraph that must not reach the row." },
            "markup": { "description": "Docs-site prose. </br></br> **NOTE**: not wanted on the row." }
          }
        }
        """;

    private static JsonTreeHarness Bound(DateHintSettings? hints = null)
        => new(Json, hints) { Schema = JsonSchemaLoader.TryParse(Schema) ?? throw new InvalidOperationException("Schema failed to load.") };

    private static (string? Label, string? Title, string? Description)? Describe(JsonTreeHarness tree, string member)
        => tree.Gutter.Describe(tree.Member(member).Row);

    [Fact]
    public void ADescriptionOnlyMemberIsLabelledByItsDescription()
    {
        var described = Describe(Bound(), "bare")!.Value;

        // Generated schemas document with `description` and no `title`, so a gutter that showed
        // only titles would show nothing - and, since the gutter cell carries the tooltip, would
        // hide the description too.
        Assert.Equal("Only a description, no title.", described.Label);
        Assert.Equal("Only a description, no title.", described.Description);

        // The fallback must not masquerade as a real title, or the tooltip would draw a
        // separator above a description it is only repeating.
        Assert.Null(described.Title);
    }

    [Fact]
    public void TheDescriptionFallbackUsesItsFirstLineOnly()
    {
        var described = Describe(Bound(), "prose")!.Value;

        Assert.Equal("First line only.", described.Label);
        Assert.Contains("Second paragraph", described.Description); // the tooltip keeps it all
    }

    [Fact]
    public void TheDescriptionFallbackStopsAtADocsSiteLineBreak()
    {
        // Generated docs break paragraphs with literal <br>/</br> as often as with a newline, and
        // the raw tag must never reach the gutter.
        Assert.Equal("Docs-site prose.", Describe(Bound(), "markup")!.Value.Label);
    }

    [Fact]
    public void ATitleIsTheLabelAndTheDescriptionStaysForTheTooltip()
    {
        var described = Describe(Bound(), "name")!.Value;

        Assert.Equal("Product name", described.Label);
        Assert.Equal("Product name", described.Title);
        Assert.Equal("What the thing is called.", described.Description);
    }

    [Fact]
    public void PrefixItemsLabelPositionalArrayElements()
    {
        var tree = Bound();
        var rows = tree.Rows();
        int csv = rows.FindIndex(r => r.Name == "csv");

        Assert.Equal("Series", tree.Gutter.Describe(rows[csv].Row)!.Value.Title);
        Assert.Equal("First series", tree.Gutter.Describe(rows.First(r => r.Marker == "0" && r.Row.ParentStart == rows[csv].Row.Node.ValueStart).Row)!.Value.Title);
        Assert.Equal("Second series", tree.Gutter.Describe(rows.First(r => r.Marker == "1" && r.Row.ParentStart == rows[csv].Row.Node.ValueStart).Row)!.Value.Title);
    }

    [Fact]
    public void AnEnumMembersLabelSupersedesThePropertyTitle()
    {
        var described = Describe(Bound(), "status")!.Value;

        Assert.Equal("Active", described.Title);
        Assert.Equal("Currently sold.", described.Description);
    }

    [Fact]
    public void RowsBelowWhereTheSchemaRunsOutAreUnlabelled()
    {
        var tree = Bound();

        Assert.Equal("Nested", Describe(tree, "nested")!.Value.Title);
        Assert.Null(Describe(tree, "deep"));
        Assert.Null(Describe(tree, "deeper"));
    }

    [Fact]
    public void WithoutASchemaNothingIsLabelledAndTheGutterTakesNoRoom()
    {
        var tree = Bound();
        Assert.NotNull(Describe(tree, "name"));
        Assert.True(tree.Gutter.Width > 0);

        tree.Schema = null;

        Assert.Null(Describe(tree, "name"));
        Assert.Equal(0, tree.Gutter.Width);
    }

    [Fact]
    public void ADateHintAndASchemaTitleShareARow()
    {
        var hints = new DateHintSettings();
        hints.SetUserDefault(DateDecodingScheme.JsSeconds);
        var tree = Bound(hints);

        Assert.NotNull(tree.Member("ts").DateHint);
        Assert.Equal("Created", Describe(tree, "ts")!.Value.Title);
    }

    [Fact]
    public void TheTooltipIsTheSameObjectWhileThePointerStaysOnARow()
    {
        var tree = Bound();
        var row = tree.Member("name").Row;

        var first = tree.Gutter.ToolTipFor(row);
        Assert.NotNull(first);
        Assert.Same(first, tree.Gutter.ToolTipFor(row));
        Assert.Null(tree.Gutter.ToolTipFor(tree.Member("deep").Row));
    }
}
