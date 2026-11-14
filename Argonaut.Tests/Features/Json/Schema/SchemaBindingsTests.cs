using Argonaut.Engine.Settings;
using Argonaut.Tests.Support;
using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>The remembered schema-per-document list: most recent first, capped, path-keyed.</summary>
public sealed class SchemaBindingsTests
{
    [Fact]
    public void Remember_KeepsTheLatestChoice_AndRemembersNoSchemaOnNull()
    {
        var bindings = new SchemaBindings();

        bindings.Remember("/docs/a.json", "/one.json");
        Assert.Equal(new SchemaBinding("/docs/a.json", "/one.json"), SchemaBindings.Find(bindings.Entries, "/docs/a.json"));

        bindings.Remember("/docs/a.json", "/two.json");
        Assert.Equal(new SchemaBinding("/docs/a.json", "/two.json"), SchemaBindings.Find(bindings.Entries, "/docs/a.json"));
        Assert.Single(bindings.Entries);

        bindings.Remember("/docs/a.json", null);
        Assert.Equal(new SchemaBinding("/docs/a.json", null), SchemaBindings.Find(bindings.Entries, "/docs/a.json"));
        Assert.Single(bindings.Entries);
    }

    [Fact]
    public void ChoseNoSchema_IsTrueOnlyForADocumentWhoseLastChoiceWasNone()
    {
        var bindings = new SchemaBindings();
        bindings.Remember("/docs/none.json", null);
        bindings.Remember("/docs/bound.json", "/one.json");

        Assert.True(SchemaBindings.ChoseNoSchema(bindings.Entries, "/docs/none.json"));
        Assert.False(SchemaBindings.ChoseNoSchema(bindings.Entries, "/docs/bound.json"));
        Assert.False(SchemaBindings.ChoseNoSchema(bindings.Entries, "/docs/never.json"));
        Assert.False(SchemaBindings.ChoseNoSchema(bindings.Entries, null));
    }

    [Fact]
    public void Choices_RoundTripThroughTheSealedBlock_AndAreNotReadableInIt()
    {
        var keys = new MemorySealingKeyStore();
        var block = new SchemaBindingsBlock();
        var bindings = new SchemaBindings(block, new AesGcmSettingsSealer(keys));
        bindings.Remember("file:/DOCS/NONE.JSON", null);
        bindings.Remember("file:/DOCS/BOUND.JSON", "/schemas/one.json", "Root");

        string json = System.Text.Json.JsonSerializer.Serialize(block, SchemaBindingsBlock.JsonTypeInfo);
        Assert.DoesNotContain("DOCS", json);
        Assert.DoesNotContain("schemas", json);

        var reread = System.Text.Json.JsonSerializer.Deserialize(json, SchemaBindingsBlock.JsonTypeInfo)!;
        var reopened = new SchemaBindings(reread, new AesGcmSettingsSealer(keys));

        Assert.Equal(bindings.Entries, reopened.Entries);
    }

    [Fact]
    public void ABlockThatWillNotOpen_StartsEmpty()
    {
        var block = new SchemaBindingsBlock();
        new SchemaBindings(block, new AesGcmSettingsSealer(new MemorySealingKeyStore())).Remember("file:/A", "/s.json");

        Assert.Empty(new SchemaBindings(block, new AesGcmSettingsSealer(new MemorySealingKeyStore())).Entries);
    }

    [Fact]
    public void Remember_RoundTripsTheBoundRoot()
    {
        var bindings = new SchemaBindings();

        bindings.Remember("/docs/a.json", "/api.json", "Booking");
        Assert.Equal("Booking", SchemaBindings.Find(bindings.Entries, "/docs/a.json")!.RootName);

        // Re-picking the schema's own root has to clear the remembered type, not keep it.
        bindings.Remember("/docs/a.json", "/api.json", null);
        Assert.Null(SchemaBindings.Find(bindings.Entries, "/docs/a.json")!.RootName);
    }

    [Fact]
    public void Remember_KeepsOtherDocuments()
    {
        var bindings = new SchemaBindings();

        bindings.Remember("/docs/a.json", "/schemas/a.json");
        bindings.Remember("/docs/b.json", "/schemas/b.json");

        Assert.Equal("/schemas/a.json", SchemaBindings.Find(bindings.Entries, "/docs/a.json")!.SchemaPath);
        Assert.Equal("/schemas/b.json", SchemaBindings.Find(bindings.Entries, "/docs/b.json")!.SchemaPath);
    }

    [Fact]
    public void Remember_CapsHistory()
    {
        var bindings = new SchemaBindings();

        for (int i = 0; i < 150; i++)
            bindings.Remember($"/docs/{i}.json", $"/schemas/{i}.json");

        // Most-recent-first with a 100-entry cap: the newest survives, the oldest is gone.
        Assert.Equal(100, bindings.Entries.Count);
        Assert.Equal("/schemas/149.json", SchemaBindings.Find(bindings.Entries, "/docs/149.json")!.SchemaPath);
        Assert.Null(SchemaBindings.Find(bindings.Entries, "/docs/0.json"));
    }

    [Fact]
    public void TheList_IsCapped()
    {
        var bindings = new SchemaBindings();

        for (int i = 0; i < 150; i++)
            bindings.Remember($"file:/d/{i}", $"/s/{i}");

        Assert.Equal(100, bindings.Entries.Count);
        Assert.Equal("file:/d/149", bindings.Entries[0].DocumentKey);
    }
}
