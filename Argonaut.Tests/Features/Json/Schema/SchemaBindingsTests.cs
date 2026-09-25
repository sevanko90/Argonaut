using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>The remembered schema-per-document list: most recent first, capped, path-keyed.</summary>
public sealed class SchemaBindingsTests
{
    [Fact]
    public void Remember_KeepsTheLatestChoice_AndForgetsOnNull()
    {
        var bindings = new SchemaBindings();

        bindings.Remember("/docs/a.json", "/one.json");
        Assert.Equal(new SchemaBinding("/docs/a.json", "/one.json"), SchemaBindings.Find(bindings.Entries, "/docs/a.json"));

        bindings.Remember("/docs/a.json", "/two.json");
        Assert.Equal(new SchemaBinding("/docs/a.json", "/two.json"), SchemaBindings.Find(bindings.Entries, "/docs/a.json"));
        Assert.Single(bindings.Entries);

        bindings.Remember("/docs/a.json", null);
        Assert.Null(SchemaBindings.Find(bindings.Entries, "/docs/a.json"));
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
    public void Entries_SetFromAHandEditedFile_IsCappedAndNeverNull()
    {
        var bindings = new SchemaBindings { Entries = null! };
        Assert.Empty(bindings.Entries);

        bindings.Entries = Enumerable.Range(0, 150).Select(i => new SchemaBinding($"/d/{i}", $"/s/{i}")).ToArray();
        Assert.Equal(100, bindings.Entries.Count);
    }
}
