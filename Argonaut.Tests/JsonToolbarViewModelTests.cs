using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
using Argonaut.Features.Json.Schema;
using Argonaut.Infrastructure;

namespace Argonaut.Tests;

/// <summary>
/// Exercises the header toolbar's binding to <see cref="DateHintSettings"/> and the
/// expand-depth callback/persistence, in isolation from any document view model.
/// AppDataPaths.RootOverride redirects ExpandDepthPreference's on-disk store to a temp dir so
/// the developer's real settings are never touched.
/// </summary>
[Collection("AppDataPaths")]
public sealed class JsonToolbarViewModelTests : IDisposable
{
    private readonly string settingsRoot;

    public JsonToolbarViewModelTests()
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

    [Fact]
    public void Ctor_SeedsIndices_FromSettingsAndInitialDepth()
    {
        var settings = new DateHintSettings();
        settings.SetUserDefault(DateDecodingScheme.JsSeconds);
        settings.SetTimeZoneMode(DateHintTimeZoneMode.Utc);

        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), initialExpandDepthIndex: 3, applyExpandDepth: _ => { });

        Assert.Equal((int)DateDecodingScheme.JsSeconds, toolbar.DateHintSchemeIndex);
        Assert.Equal((int)DateHintTimeZoneMode.Utc, toolbar.TimeZoneModeIndex);
        Assert.Equal(3, toolbar.ExpandDepthIndex);
    }

    [Fact]
    public void DateHintSchemeIndex_Set_UpdatesSettings_AndLatchesUserSelected()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), 0, _ => { });

        toolbar.DateHintSchemeIndex = (int)DateDecodingScheme.KeepaMinutes;

        Assert.Equal(DateDecodingScheme.KeepaMinutes, settings.FileDefaultScheme);
        Assert.True(settings.IsUserSelected);
    }

    [Fact]
    public void TimeZoneModeIndex_Set_UpdatesSettings()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), 0, _ => { });

        toolbar.TimeZoneModeIndex = (int)DateHintTimeZoneMode.Utc;

        Assert.Equal(DateHintTimeZoneMode.Utc, settings.TimeZoneMode);
    }

    [Fact]
    public void NegativeIndexAssignments_AreIgnored()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), 0, _ => { });

        toolbar.DateHintSchemeIndex = -1;
        toolbar.TimeZoneModeIndex = -1;
        toolbar.ExpandDepthIndex = -1;

        Assert.Equal(DateDecodingScheme.Off, settings.FileDefaultScheme);
        Assert.False(settings.IsUserSelected);
        Assert.Equal(DateHintTimeZoneMode.Local, settings.TimeZoneMode);
    }

    [Fact]
    public void InferredDefault_SyncsComboWithoutLatchingUserSelected_AndReassigningSameValueStaysUnlatched()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), 0, _ => { });

        // Background inference lands - the combo should follow without marking IsUserSelected.
        settings.TrySetInferredDefault(DateDecodingScheme.JsSeconds);
        Assert.Equal((int)DateDecodingScheme.JsSeconds, toolbar.DateHintSchemeIndex);
        Assert.False(settings.IsUserSelected);

        // Simulates the two-way binding writing the (already-synced) value back into the VM -
        // must be a no-op via the SetField equality guard, not a redundant SetUserDefault call
        // that would incorrectly latch IsUserSelected.
        toolbar.DateHintSchemeIndex = (int)DateDecodingScheme.JsSeconds;

        Assert.False(settings.IsUserSelected);
    }

    private static SchemaCatalogEntry WriteSchema(string name)
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, $$"""{ "title": "{{name}}" }""");
        return new SchemaCatalogEntry(name, path, IsUser: true);
    }

    [Fact]
    public void SchemaItems_StartEmpty_WithNoSchemaAndOpenFolderOnly()
    {
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), new JsonSchemaSettings(), 0, _ => { });

        Assert.Equal(new[] { JsonToolbarViewModel.NoSchemaLabel, JsonToolbarViewModel.OpenSchemaFolderLabel }, toolbar.SchemaItems);
        Assert.Equal(0, toolbar.SelectedSchemaIndex);
    }

    [Fact]
    public void SchemaEntries_ArrivingLate_RebuildTheCombo()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        schemaSettings.SetEntries(new[] { WriteSchema("alpha"), WriteSchema("beta") });

        Assert.Equal(
            new[] { JsonToolbarViewModel.NoSchemaLabel, "alpha", "beta", JsonToolbarViewModel.OpenSchemaFolderLabel },
            toolbar.SchemaItems);
    }

    [Fact]
    public async Task SelectedSchemaIndex_Set_BindsThatSchema_AndZeroClearsIt()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteSchema("alpha");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        toolbar.SelectedSchemaIndex = 1;
        await WaitForDocumentAsync(schemaSettings);

        Assert.Equal(entry, schemaSettings.SelectedEntry);
        Assert.NotNull(schemaSettings.Document);

        toolbar.SelectedSchemaIndex = 0;

        Assert.Null(schemaSettings.SelectedEntry);
        Assert.Null(schemaSettings.Document);
    }

    [Fact]
    public async Task SidecarAutoSelection_LightsUpTheCombo()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteSchema("sidecar");
        schemaSettings.SetEntries(new[] { WriteSchema("other"), entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        await schemaSettings.SelectAsync(entry);

        Assert.Equal(2, toolbar.SelectedSchemaIndex);
    }

    [Fact]
    public void OpenSchemaFolderItem_OpensTheFolder_AndRevertsTheSelection()
    {
        var opened = new List<string>();
        JsonSchemaCatalog.OpenDirectoryOverride = opened.Add;
        try
        {
            var schemaSettings = new JsonSchemaSettings();
            schemaSettings.SetEntries(new[] { WriteSchema("alpha") });
            var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

            toolbar.SelectedSchemaIndex = toolbar.SchemaItems.Count - 1;

            Assert.Equal(new[] { JsonSchemaCatalog.GetUserDirectory() }, opened);
            Assert.Equal(0, toolbar.SelectedSchemaIndex);
            Assert.Null(schemaSettings.SelectedEntry);
        }
        finally
        {
            JsonSchemaCatalog.OpenDirectoryOverride = null;
        }
    }

    [Fact]
    public void NegativeSchemaIndex_DuringItemSwap_DoesNotClearTheBinding()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteSchema("alpha");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        toolbar.SelectedSchemaIndex = 1;

        toolbar.SelectedSchemaIndex = -1;

        Assert.Equal(1, toolbar.SelectedSchemaIndex);
        Assert.Equal(entry, schemaSettings.SelectedEntry);
    }

    /// <summary>The combo setter fires SelectAsync without awaiting it (a property setter can't),
    /// so tests have to wait for the parse to land.</summary>
    private static async Task WaitForDocumentAsync(JsonSchemaSettings settings)
    {
        for (int i = 0; i < 100 && settings.Document is null; i++)
            await Task.Delay(10);
    }

    [Fact]
    public void ExpandDepthIndex_Set_PersistsAndInvokesCallback()
    {
        var settings = new DateHintSettings();
        var applied = new List<int>();
        var toolbar = new JsonToolbarViewModel(settings, new JsonSchemaSettings(), 0, applied.Add);

        toolbar.ExpandDepthIndex = 4;

        Assert.Equal(4, ExpandDepthPreference.Load());
        Assert.Equal(new[] { 4 }, applied);

        toolbar.ExpandDepthIndex = 4;

        Assert.Equal(new[] { 4 }, applied);
    }
}
