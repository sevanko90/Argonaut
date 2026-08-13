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

    /// <summary>Drives OpenUserDirectory with the file-manager launch stubbed out.</summary>
    private static void OpenSchemaFolder()
    {
        JsonSchemaCatalog.OpenDirectoryOverride = _ => { };
        try
        {
            JsonSchemaCatalog.OpenUserDirectory();
        }
        finally
        {
            JsonSchemaCatalog.OpenDirectoryOverride = null;
        }
    }

    /// <summary>The example is a shipped file, not a string constant, so a rename or a dropped
    /// Content glob would silently stop the seeding. Fail loudly at the source instead.</summary>
    [Fact]
    public void GeoJsonSchema_ShipsWithTheApp()
    {
        Assert.True(File.Exists(Path.Combine(JsonSchemaCatalog.GetBundledDirectory(), JsonSchemaExample.BundledFileName)));
    }

    /// <summary>Unlike the copy it seeds, the bundled original is a normal, bindable schema.</summary>
    [Fact]
    public void BundledGeoJsonSchema_IsOfferedInTheDropdown()
    {
        var geojson = Assert.Single(JsonSchemaCatalog.Enumerate(), e => e.DisplayName == "geojson");

        Assert.False(geojson.IsUser);
        Assert.NotNull(JsonSchemaLoader.TryLoadFile(geojson.FilePath));
    }

    [Fact]
    public void OpeningTheSchemaFolder_SeedsTheExampleCopy()
    {
        OpenSchemaFolder();

        Assert.True(File.Exists(Path.Combine(JsonSchemaCatalog.GetUserDirectory(), JsonSchemaExample.UserCopyFileName)));
    }

    /// <summary>The seeded copy is the same schema as the bundled one, so the whole point of the
    /// .example.json suffix is that it doesn't show up as a second, duplicate dropdown entry.</summary>
    [Fact]
    public void SeededExampleCopy_DoesNotDuplicateTheBundledEntry()
    {
        OpenSchemaFolder();

        var entries = JsonSchemaCatalog.Enumerate();

        var geojson = Assert.Single(entries, e => e.DisplayName == "geojson");
        Assert.False(geojson.IsUser); // still the bundled one, not the user copy
        Assert.DoesNotContain(entries,
            e => e.FilePath.EndsWith(JsonSchemaCatalog.ExampleSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Renaming the copy to the bundled name is the documented way to take it over, and
    /// relies on the user-shadows-bundled rule.</summary>
    [Fact]
    public void RenamedExampleCopy_ShadowsTheBundledSchema()
    {
        OpenSchemaFolder();
        string directory = JsonSchemaCatalog.GetUserDirectory();
        string renamed = Path.Combine(directory, JsonSchemaExample.BundledFileName);
        File.Move(Path.Combine(directory, JsonSchemaExample.UserCopyFileName), renamed);

        var geojson = Assert.Single(JsonSchemaCatalog.Enumerate(), e => e.DisplayName == "geojson");

        Assert.True(geojson.IsUser);
        Assert.Equal(renamed, geojson.FilePath);
    }

    [Fact]
    public void OpeningTheSchemaFolder_NeverClobbersAnEditedExample()
    {
        OpenSchemaFolder();
        string path = Path.Combine(JsonSchemaCatalog.GetUserDirectory(), JsonSchemaExample.UserCopyFileName);
        File.WriteAllText(path, """{ "title": "Mine now" }""");

        OpenSchemaFolder();

        Assert.Equal("""{ "title": "Mine now" }""", File.ReadAllText(path));
    }

    [Fact]
    public void ExampleSchemas_AreHiddenFromTheDropdown()
    {
        OpenSchemaFolder();
        WriteUserSchema("real-one.json");

        var entries = JsonSchemaCatalog.Enumerate();

        Assert.Contains(entries, e => e.DisplayName == "real-one");
        Assert.DoesNotContain(entries, e => e.FilePath.EndsWith(JsonSchemaCatalog.ExampleSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The example is handed to users as the thing to copy, so it has to be a schema that actually
    /// works - not just valid JSON. Loads the seeded copy through the real loader and checks the
    /// constructs it teaches (prefixItems, oneOf/const labels, and a recursive $ref) all resolve.
    /// </summary>
    [Fact]
    public void ShippedExample_IsAWorkingSchema()
    {
        OpenSchemaFolder();
        string path = Path.Combine(JsonSchemaCatalog.GetUserDirectory(), JsonSchemaExample.UserCopyFileName);

        var schema = JsonSchemaLoader.TryLoadFile(path);
        Assert.NotNull(schema);

        int bbox = schema!.ResolveMember(schema.RootId, "bbox"u8);
        Assert.Equal("Min longitude", schema.GetTitle(schema.ResolveElement(bbox, 0)));
        Assert.Equal("Max latitude", schema.GetTitle(schema.ResolveElement(bbox, 3)));

        int feature = schema.ResolveElement(schema.ResolveMember(schema.RootId, "features"u8), 0);
        Assert.Null(schema.GetTitle(feature)); // deliberately untitled - it labels every element

        // The example $refs a location outside $defs; that has to resolve or it teaches a broken pattern.
        int featureBbox = schema.ResolveMember(feature, "bbox"u8);
        Assert.Equal("Min longitude", schema.GetTitle(schema.ResolveElement(featureBbox, 0)));

        // Recursion: a geometry collection's members resolve back to the geometry definition.
        int geometry = schema.ResolveMember(feature, "geometry"u8);
        int nested = schema.ResolveElement(schema.ResolveMember(geometry, "geometries"u8), 0);
        Assert.Equal("Geometry", schema.GetTitle(nested));

        int geometryType = schema.ResolveMember(nested, "type"u8);
        Assert.True(schema.TryGetEnumLabel(geometryType, "\"LineString\"", JsonTokenKind.String, out var title, out _));
        Assert.Equal("Line", title);
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
