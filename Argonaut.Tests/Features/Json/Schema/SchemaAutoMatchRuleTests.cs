using System.Text;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>
/// Covers the bar a schema has to clear to be bound on its own when a document opens - stricter
/// than the picker's ranking, because nobody chose it - and the cases where the honest answer is
/// to bind nothing.
/// </summary>
public class SchemaAutoMatchRuleTests
{
    private static SchemaFingerprint Fingerprint(string json)
        => SchemaFingerprint.Of(JsonSchemaLoader.TryParse(json) ?? throw new InvalidOperationException("Schema failed to load."));

    private static SchemaFingerprint Bundled(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, JsonSchemaCatalog.BundledFolderName, name);
        return SchemaFingerprint.Of(JsonSchemaLoader.TryLoadFile(path) ?? throw new InvalidOperationException($"Bundled schema {name} failed to load."));
    }

    private static IReadOnlyList<byte[]> Keys(params string[] names)
        => names.Select(Encoding.UTF8.GetBytes).ToArray();

    private static IReadOnlyList<byte[]> Keys(IEnumerable<string> names)
        => names.Select(Encoding.UTF8.GetBytes).ToArray();

    private static string Properties(params string[] names)
        => string.Join(", ", names.Select(n => $"\"{n}\": {{}}"));

    private static readonly string[] Order = ["id", "customer", "lines", "total", "currency", "placedAt", "status", "notes"];

    private static readonly string OrderSchema = $$"""{ "properties": { {{Properties(Order)}} } }""";

    [Fact]
    public void BroadEvidence_Binds()
    {
        var match = SchemaAutoMatchRule.Choose([Fingerprint(OrderSchema)], Keys(Order));

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.SchemaIndex);
        Assert.Null(match.Value.Match.Name);
    }

    [Fact]
    public void FewerThanSixSharedNames_WithoutRequired_DoesNotBind()
    {
        var schema = Fingerprint($$"""{ "properties": { {{Properties("id", "name", "email")}} } }""");

        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys("id", "name")));
        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys("id", "name", "email")));
    }

    /// <summary>A partial schema: everything it declares is present, though the document carries
    /// far more - the shape of a hand-written schema for a large API object.</summary>
    [Fact]
    public void PreciseSubset_Binds()
    {
        var extra = Enumerable.Range(0, 40).Select(i => $"extra{i}");

        var match = SchemaAutoMatchRule.Choose([Fingerprint(OrderSchema)], Keys(Order.Concat(extra)));

        Assert.NotNull(match);
    }

    [Fact]
    public void BroadButWeak_DoesNotBind()
    {
        // 6 shared names, but they explain half the document and are 6 of the schema's 12.
        string[] schemaNames = [.. Order, "a", "b", "c", "d"];
        string[] documentNames = [.. Order[..6], "w", "x", "y", "z", "q", "r"];
        var schema = Fingerprint($$"""{ "properties": { {{Properties(schemaNames)}} } }""");

        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys(documentNames)));
    }

    [Fact]
    public void SmallTypeWithRequired_BindsWhenItExplainsEveryKey()
    {
        var schema = Fingerprint($$"""{ "properties": { {{Properties("type", "features", "bbox")}} }, "required": ["type", "features"] }""");

        Assert.NotNull(SchemaAutoMatchRule.Choose([schema], Keys("type", "features")));
        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys("type", "features", "somethingElse")));
    }

    [Fact]
    public void SmallTypeWithOneRequiredName_DoesNotBind()
    {
        var schema = Fingerprint($$"""{ "properties": { {{Properties("type", "value")}} }, "required": ["type"] }""");

        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys("type", "value")));
    }

    [Fact]
    public void MissingRequiredName_Vetoes()
    {
        var schema = Fingerprint($$"""{ "properties": { {{Properties(Order)}} }, "required": ["id", "signature"] }""");

        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys(Order)));
    }

    [Fact]
    public void TwoSchemasFittingEquallyWell_BindNeither()
    {
        var match = SchemaAutoMatchRule.Choose([Fingerprint(OrderSchema), Fingerprint(OrderSchema)], Keys(Order));

        Assert.Null(match);
    }

    [Fact]
    public void ClearlyBetterSchema_WinsOverAPlausibleOne()
    {
        // The second schema shares only half of the order's names: plausible for the picker, but
        // well behind.
        var weaker = Fingerprint($$"""{ "properties": { {{Properties([.. Order[..4], "p", "q", "r", "s"])}} } }""");

        var match = SchemaAutoMatchRule.Choose([weaker, Fingerprint(OrderSchema)], Keys(Order));

        Assert.NotNull(match);
        Assert.Equal(1, match.Value.SchemaIndex);
    }

    [Fact]
    public void TypesTiedInsideOneSchema_DoNotBind()
    {
        var schema = Fingerprint($$"""
            { "$defs": {
                "Commit": { "properties": { {{Properties(Order)}} } },
                "Retrieve": { "properties": { {{Properties(Order)}} } }
            } }
            """);

        Assert.Null(SchemaAutoMatchRule.Choose([schema], Keys(Order)));
    }

    [Fact]
    public void NamedType_IsReportedByName()
    {
        var schema = Fingerprint($$"""
            { "$defs": {
                "Order": { "properties": { {{Properties(Order)}} } },
                "Address": { "properties": { {{Properties("line1", "city", "postcode")}} } }
            } }
            """);

        var match = SchemaAutoMatchRule.Choose([schema], Keys(Order));

        Assert.Equal("Order", match?.Match.Name);
    }

    [Fact]
    public void NoKeys_MatchNothing()
    {
        Assert.Null(SchemaAutoMatchRule.Choose([Fingerprint(OrderSchema)], Keys()));
        Assert.Null(SchemaAutoMatchRule.Choose([], Keys(Order)));
    }

    [Fact]
    public void BundledGeoJson_MatchesAFeatureCollection()
    {
        var match = SchemaAutoMatchRule.Choose([Bundled("geojson.json"), Bundled("keepa-product.json")], Keys("type", "features"));

        Assert.NotNull(match);
        Assert.Equal(0, match.Value.SchemaIndex);
        Assert.Null(match.Value.Match.Name);
    }

    [Fact]
    public void BundledGeoJson_MatchesASingleFeatureAsItsFeatureType()
    {
        var match = SchemaAutoMatchRule.Choose([Bundled("geojson.json"), Bundled("keepa-product.json")], Keys("type", "geometry", "properties", "id"));

        Assert.Equal("feature", match?.Match.Name);
    }

    [Fact]
    public void BundledKeepa_MatchesAProduct()
    {
        var keepa = Bundled("keepa-product.json");
        var product = keepa.Types.Single(t => t.Name == "product");
        var documentNames = product.PropertyKeys.Take(80).Select(Encoding.UTF8.GetString).Concat(["undocumentedField"]);

        var match = SchemaAutoMatchRule.Choose([Bundled("geojson.json"), keepa], Keys(documentNames));

        Assert.NotNull(match);
        Assert.Equal(1, match.Value.SchemaIndex);
        Assert.Equal("product", match.Value.Match.Name);
    }

    [Fact]
    public void Fingerprint_ListsTheUsableRootThenNamedRoots()
    {
        var schema = Fingerprint("""
            {
              "properties": { "a": {} },
              "required": ["a"],
              "$defs": { "thing": { "properties": { "b": {}, "c": {} } } }
            }
            """);

        Assert.Collection(schema.Types,
            root =>
            {
                Assert.Null(root.Name);
                Assert.Equal(["a"], root.RequiredKeys.Select(Encoding.UTF8.GetString));
            },
            thing =>
            {
                Assert.Equal("thing", thing.Name);
                Assert.Equal(["b", "c"], thing.PropertyKeys.Select(Encoding.UTF8.GetString));
                Assert.Empty(thing.RequiredKeys);
            });
    }
}
