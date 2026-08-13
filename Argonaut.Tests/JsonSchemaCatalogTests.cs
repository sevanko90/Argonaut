using Argonaut.Features.Json;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Covers the bundled/user schema merge, user-shadows-bundled, and the per-document gather
/// (sidecar and remembered selection). AppDataPaths.RootOverride redirects the user schema folder
/// and the remembered-selection file into a temp dir, so the developer's real settings and
/// schemas are never touched.
/// </summary>
[Collection("AppDataPaths")]
public sealed class JsonSchemaCatalogTests : IDisposable
{
    private readonly string settingsRoot;

    public JsonSchemaCatalogTests()
    {
        settingsRoot = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
        AppDataPaths.RootOverride = settingsRoot;
    }

    public void Dispose()
    {
        AppDataPaths.RootOverride = null;
        try { if (Directory.Exists(settingsRoot)) Directory.Delete(settingsRoot, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    private static string WriteUserSchema(string fileName, string content = """{ "title": "T" }""")
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Enumerate_IncludesBundledSchemas()
    {
        var entries = JsonSchemaCatalog.Enumerate();

        var keepa = Assert.Single(entries, e => e.DisplayName == "keepa-product");
        Assert.False(keepa.IsUser);
    }

    [Fact]
    public void BundledKeepaSchema_ActuallyParses()
    {
        var keepa = Assert.Single(JsonSchemaCatalog.Enumerate(), e => e.DisplayName == "keepa-product");

        Assert.NotNull(JsonSchemaLoader.TryLoadFile(keepa.FilePath));
    }

    /// <summary>
    /// Walks the shipped Keepa schema the way the row pipeline does - down through two $ref hops
    /// and two arrays - so a typo in a pointer, or an enum written in a form the loader doesn't
    /// recognise, fails here rather than silently showing no labels in the tree.
    /// </summary>
    [Fact]
    public void BundledKeepaSchema_LabelsOffersThroughItsRefs()
    {
        var keepa = Assert.Single(JsonSchemaCatalog.Enumerate(), e => e.DisplayName == "keepa-product");
        var schema = JsonSchemaLoader.TryLoadFile(keepa.FilePath)!;

        int products = schema.ResolveMember(schema.RootId, "products"u8);
        int product = schema.ResolveElement(products, 0);
        int offers = schema.ResolveMember(product, "offers"u8);
        int offer = schema.ResolveElement(offers, 0);
        int condition = schema.ResolveMember(offer, "condition"u8);

        Assert.Equal("Marketplace offer", schema.GetTitle(offer));
        Assert.Equal("Price and shipping history", schema.GetTitle(schema.ResolveMember(offer, "offerCSV"u8)));
        Assert.Equal("Stock history", schema.GetTitle(schema.ResolveMember(offer, "stockCSV"u8)));

        Assert.True(schema.TryGetEnumLabel(condition, "3", JsonTokenKind.Number, out var title, out _));
        Assert.Equal("Used - Very Good", title);

        // The csv prefixItems layout is the motivating positional case - slot 3 must stay put.
        int csv = schema.ResolveMember(product, "csv"u8);
        Assert.Equal("Sales rank", schema.GetTitle(schema.ResolveElement(csv, 3)));
    }

    [Fact]
    public void Enumerate_IncludesUserSchemas_SortedByName()
    {
        WriteUserSchema("zzz-last.json");
        WriteUserSchema("aaa-first.json");

        var entries = JsonSchemaCatalog.Enumerate();

        Assert.True(entries[0].DisplayName == "aaa-first");
        Assert.True(entries[^1].DisplayName == "zzz-last");
        Assert.True(Assert.Single(entries, e => e.DisplayName == "aaa-first").IsUser);
    }

    [Fact]
    public void UserSchema_ShadowsBundledOfTheSameName()
    {
        string userCopy = WriteUserSchema("keepa-product.json");

        var keepa = Assert.Single(JsonSchemaCatalog.Enumerate(), e => e.DisplayName == "keepa-product");

        Assert.True(keepa.IsUser);
        Assert.Equal(userCopy, keepa.FilePath);
    }

    [Fact]
    public void Enumerate_WithNoUserFolder_StillReturnsBundled()
    {
        Assert.False(Directory.Exists(JsonSchemaCatalog.GetUserDirectory()));

        Assert.NotEmpty(JsonSchemaCatalog.Enumerate());
    }

    [Fact]
    public void GatherForDocument_PrefersSidecar()
    {
        string document = Path.GetTempFileName();
        string sidecar = document + JsonSchemaCatalog.SidecarSuffix;
        try
        {
            File.WriteAllText(sidecar, """{ "title": "Sidecar" }""");

            var (entries, preselected) = JsonSchemaCatalog.GatherForDocument(document);

            Assert.Equal(sidecar, preselected!.Value.FilePath);
            Assert.Contains(entries, e => e.FilePath == sidecar);
        }
        finally
        {
            File.Delete(sidecar);
            File.Delete(document);
        }
    }

    [Fact]
    public void GatherForDocument_WithNoSidecarOrMemory_PreselectsNothing()
    {
        string document = Path.GetTempFileName();
        try
        {
            var (_, preselected) = JsonSchemaCatalog.GatherForDocument(document);

            Assert.Null(preselected);
        }
        finally
        {
            File.Delete(document);
        }
    }

    [Fact]
    public void GatherForDocument_RestoresRememberedCatalogEntry()
    {
        string document = Path.GetTempFileName();
        try
        {
            string remembered = WriteUserSchema("remembered.json");
            SchemaSelectionPreference.Save(document, remembered);

            var (_, preselected) = JsonSchemaCatalog.GatherForDocument(document);

            Assert.Equal("remembered", preselected!.Value.DisplayName);
            Assert.Equal(remembered, preselected.Value.FilePath);
        }
        finally
        {
            File.Delete(document);
        }
    }

    [Fact]
    public void GatherForDocument_RestoresRememberedSchemaOutsideTheCatalogFolders()
    {
        string document = Path.GetTempFileName();
        string loose = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(loose, """{ "title": "Loose" }""");
            SchemaSelectionPreference.Save(document, loose);

            var (entries, preselected) = JsonSchemaCatalog.GatherForDocument(document);

            Assert.Equal(loose, preselected!.Value.FilePath);
            Assert.Contains(entries, e => e.FilePath == loose);
        }
        finally
        {
            File.Delete(loose);
            File.Delete(document);
        }
    }

    [Fact]
    public void GatherForDocument_IgnoresRememberedSchemaThatNoLongerExists()
    {
        string document = Path.GetTempFileName();
        try
        {
            SchemaSelectionPreference.Save(document, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"));

            var (_, preselected) = JsonSchemaCatalog.GatherForDocument(document);

            Assert.Null(preselected);
        }
        finally
        {
            File.Delete(document);
        }
    }

    [Fact]
    public void SchemaSelectionPreference_SavesLatestChoice_AndForgetsOnNull()
    {
        string document = Path.GetTempFileName();
        try
        {
            SchemaSelectionPreference.Save(document, "/one.json");
            Assert.Equal("/one.json", SchemaSelectionPreference.Load(document));

            SchemaSelectionPreference.Save(document, "/two.json");
            Assert.Equal("/two.json", SchemaSelectionPreference.Load(document));

            SchemaSelectionPreference.Save(document, null);
            Assert.Null(SchemaSelectionPreference.Load(document));
        }
        finally
        {
            File.Delete(document);
        }
    }

    [Fact]
    public void SchemaSelectionPreference_KeepsOtherDocuments()
    {
        SchemaSelectionPreference.Save("/docs/a.json", "/schemas/a.json");
        SchemaSelectionPreference.Save("/docs/b.json", "/schemas/b.json");

        Assert.Equal("/schemas/a.json", SchemaSelectionPreference.Load("/docs/a.json"));
        Assert.Equal("/schemas/b.json", SchemaSelectionPreference.Load("/docs/b.json"));
    }

    [Fact]
    public void SchemaSelectionPreference_CapsHistory()
    {
        for (int i = 0; i < 150; i++)
            SchemaSelectionPreference.Save($"/docs/{i}.json", $"/schemas/{i}.json");

        // Most-recent-first with a 100-entry cap: the newest survives, the oldest is gone.
        Assert.Equal("/schemas/149.json", SchemaSelectionPreference.Load("/docs/149.json"));
        Assert.Null(SchemaSelectionPreference.Load("/docs/0.json"));
    }
}
