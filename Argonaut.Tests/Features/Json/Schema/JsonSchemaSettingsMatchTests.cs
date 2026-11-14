using Argonaut.Features.Json.Schema;

namespace Argonaut.Tests.Features.Json.Schema;

/// <summary>
/// A matched schema against a chosen one in <see cref="JsonSchemaSettings"/>: a match binds only
/// where nothing was chosen, and becomes the user's choice the moment they pick a type in it.
/// </summary>
public sealed class JsonSchemaSettingsMatchTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
    private readonly SchemaCatalogEntry order;

    public JsonSchemaSettingsMatchTests()
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "order.json");
        File.WriteAllText(path, """
            { "$defs": {
                "Order": { "properties": { "id": {}, "total": {} } },
                "Line": { "properties": { "sku": {}, "quantity": {} } }
            } }
            """);
        order = new SchemaCatalogEntry("order", path, IsUser: true);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }

    [Fact]
    public async Task AMatch_BindsAndSaysSo()
    {
        var settings = new JsonSchemaSettings();

        await settings.SelectMatchedAsync(order, "Line");

        Assert.Equal(order, settings.SelectedEntry);
        Assert.Equal("Line", settings.SelectedRootName);
        Assert.True(settings.IsMatched);
        Assert.False(settings.IsRootExplicitlyChosen);
    }

    [Fact]
    public async Task AMatch_AfterNoSchemaWasChosen_BindsNothing()
    {
        var settings = new JsonSchemaSettings();
        await settings.SelectAsync(null);

        await settings.SelectMatchedAsync(order, null);

        Assert.Null(settings.SelectedEntry);
        Assert.False(settings.IsMatched);
    }

    [Fact]
    public async Task AMatch_NeverReplacesAChosenSchema()
    {
        var settings = new JsonSchemaSettings();
        var other = order with { DisplayName = "other" };
        await settings.SelectAsync(other);

        await settings.SelectMatchedAsync(order, null);

        Assert.Equal(other, settings.SelectedEntry);
    }

    [Fact]
    public async Task PickingATypeInAMatch_MakesItAChoice()
    {
        var settings = new JsonSchemaSettings();
        await settings.SelectMatchedAsync(order, "Line");

        settings.SelectRoot("Order");

        Assert.False(settings.IsMatched);
        Assert.True(settings.IsSchemaChosen);
        Assert.True(settings.IsRootExplicitlyChosen);
    }

    [Fact]
    public async Task PickingASchema_EndsTheMatch()
    {
        var settings = new JsonSchemaSettings();
        await settings.SelectMatchedAsync(order, null);

        await settings.SelectAsync(order);

        Assert.False(settings.IsMatched);
    }
}
