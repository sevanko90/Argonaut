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

    /// <summary>Acting on a schema/type pick is deferred a dispatcher turn (see
    /// <see cref="UiDeferral"/>); this stands in for that turn.</summary>
    private readonly DeferredUiScope ui = new();

    public JsonToolbarViewModelTests()
    {
        settingsRoot = Path.Combine(Path.GetTempPath(), "ArgonautTests", Guid.NewGuid().ToString("N"));
        AppDataPaths.RootOverride = settingsRoot;
    }

    public void Dispose()
    {
        ui.Dispose();
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

    /// <summary>An OpenAPI document: several named roots, and a root of its own that isn't a
    /// schema - the case the root picker exists for.</summary>
    private static SchemaCatalogEntry WriteOpenApiSchema(string name)
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, """
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "Booking": { "title": "Booking", "properties": { "reference": {}, "passengers": {}, "flights": {} } },
                  "Passenger": { "title": "Passenger", "properties": { "surname": {}, "dateOfBirth": {} } }
                }
              }
            }
            """);
        return new SchemaCatalogEntry(name, path, IsUser: true);
    }

    /// <summary>Opens the flyout and picks the entry at <paramref name="index"/>, as the view does.</summary>
    private void PickSchema(JsonToolbarViewModel toolbar, int index)
    {
        toolbar.IsSchemaFlyoutOpen = true;
        SelectSchema(toolbar, index);
    }

    /// <summary>Sets the bound index and lets the deferred work run: the setter defers acting on
    /// the choice by a turn (see <see cref="UiDeferral"/>), which the running app's dispatcher does
    /// for free and a test has to do by hand.</summary>
    private void SelectSchema(JsonToolbarViewModel toolbar, int index)
    {
        toolbar.SelectedSchemaIndex = index;
        ui.Pump();
    }

    private static void Match(JsonSchemaSettings settings, params string[] documentKeys)
        => settings.SetRootMatches(JsonSchemaRootMatcher.Rank(
            settings.Document!,
            documentKeys.Select(System.Text.Encoding.UTF8.GetBytes).ToArray()));

    [Fact]
    public async Task PickingASchema_LeavesTheFlyoutOpenUntilTheTypeIsKnown()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteOpenApiSchema("api");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        // Which type fits is only known once the document has been scored, which happens later
        // and elsewhere; closing now would hide the list the user may still need.
        Assert.True(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task AnUnambiguousMatch_ClosesTheFlyout()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteOpenApiSchema("api") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        Match(schemaSettings, "reference", "passengers", "flights");

        Assert.False(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task AnAmbiguousMatch_KeepsTheFlyoutOpen()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteAmbiguousSchema("ambiguous") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        Match(schemaSettings, "booking", "warnings");

        // Two candidates score identically - that is exactly the choice the user has to make, so
        // the list has to stay up.
        Assert.True(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task ASchemaWithNoTypes_ClosesTheFlyoutImmediately()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteSchema("geojson") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        // Nothing further to choose, so leaving it open would just need dismissing.
        Assert.False(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task ClearingTheSchema_ClosesTheFlyout()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteOpenApiSchema("api") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        toolbar.IsSchemaFlyoutOpen = true;
        SelectSchema(toolbar, 0);

        Assert.False(toolbar.IsSchemaFlyoutOpen);
        Assert.Null(schemaSettings.SelectedEntry);
    }

    [Fact]
    public async Task PickingAType_ClosesTheFlyout()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteAmbiguousSchema("ambiguous") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);
        Assert.True(toolbar.IsSchemaFlyoutOpen);

        var picker = toolbar.SchemaRootPicker;
        picker.SelectedPick = picker.Picks.First(p => p.IsSelectable);
        ui.Pump();

        Assert.False(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task LateMatches_NeverShutAFlyoutTheUserReopened()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteOpenApiSchema("api") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);
        Match(schemaSettings, "reference", "passengers", "flights");
        Assert.False(toolbar.IsSchemaFlyoutOpen);

        // Indexing finishing re-scores minutes later; the user is now browsing types by hand.
        toolbar.IsSchemaFlyoutOpen = true;
        Match(schemaSettings, "reference", "passengers", "flights");

        Assert.True(toolbar.IsSchemaFlyoutOpen);
    }

    [Fact]
    public async Task ClosingTheFlyout_ClearsTheTypeFilter()
    {
        var schemaSettings = new JsonSchemaSettings();
        schemaSettings.SetEntries(new[] { WriteOpenApiSchema("api") });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        PickSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        toolbar.SchemaRootPicker.Filter = "pass";
        toolbar.IsSchemaFlyoutOpen = false;

        // Reopening starts from the whole list, not from whatever was last typed.
        Assert.Equal(string.Empty, toolbar.SchemaRootPicker.Filter);
    }

    [Fact]
    public void OpeningTheFlyout_RefreshesTheSchemaCatalog()
    {
        // The catalog is gathered once, at document-open time, so a schema dropped into the
        // user folder mid-session would otherwise only appear after a restart. Opening the
        // flyout re-lists it instead.
        int refreshCount = 0;
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { },
            refreshSchemaEntries: () => { refreshCount++; return Task.CompletedTask; });

        toolbar.IsSchemaFlyoutOpen = true;
        Assert.Equal(1, refreshCount);

        // Closing, and setting the same value again, aren't opens.
        toolbar.IsSchemaFlyoutOpen = false;
        Assert.Equal(1, refreshCount);

        toolbar.IsSchemaFlyoutOpen = true;
        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public void SchemaButtonText_ReadsNoSchema_WhenNothingIsBound()
    {
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), new JsonSchemaSettings(), 0, _ => { });

        Assert.Equal(JsonToolbarViewModel.NoSchemaLabel, toolbar.SchemaButtonText);
    }

    [Fact]
    public async Task SchemaButtonText_IsJustTheFile_ForASingleSchemaFile()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        await schemaSettings.SelectAsync(WriteSchema("geojson"));

        // Nothing to choose within it, so a "file › type" label would be noise.
        Assert.Equal("geojson", toolbar.SchemaButtonText);
    }

    [Fact]
    public async Task SchemaButtonText_SpansFileAndType_ForAMultiRootSchema()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        await schemaSettings.SelectAsync(WriteOpenApiSchema("api"));

        Assert.Equal("api" + JsonToolbarViewModel.SchemaPathSeparator + "Booking", toolbar.SchemaButtonText);

        schemaSettings.SelectRoot("Passenger");

        Assert.Equal("api" + JsonToolbarViewModel.SchemaPathSeparator + "Passenger", toolbar.SchemaButtonText);
    }

    [Fact]
    public async Task SchemaButtonText_ReturnsToNoSchema_WhenCleared()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteOpenApiSchema("api");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        SelectSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);
        Assert.StartsWith("api", toolbar.SchemaButtonText);

        SelectSchema(toolbar, 0);

        Assert.Equal(JsonToolbarViewModel.NoSchemaLabel, toolbar.SchemaButtonText);
    }

    /// <summary>Two types that are indistinguishable on property names - the case no name-based
    /// scorer can settle, so the user must.</summary>
    private static SchemaCatalogEntry WriteAmbiguousSchema(string name)
    {
        string directory = JsonSchemaCatalog.EnsureUserDirectory();
        string path = Path.Combine(directory, name + ".json");
        File.WriteAllText(path, """
            {
              "openapi": "3.0.3",
              "components": {
                "schemas": {
                  "CommitBookingResponse": { "properties": { "booking": {}, "warnings": {} } },
                  "RetrieveBookingResponse": { "properties": { "booking": {}, "warnings": {} } }
                }
              }
            }
            """);
        return new SchemaCatalogEntry(name, path, IsUser: true);
    }

    [Fact]
    public void SchemaRootPicker_IsHidden_WithNoSchemaBound()
    {
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), new JsonSchemaSettings(), 0, _ => { });

        Assert.False(toolbar.SchemaRootPicker.IsApplicable);
        Assert.Empty(toolbar.SchemaRootPicker.Picks);
    }

    [Fact]
    public async Task SchemaRootPicker_IsHidden_ForASingleSchemaFile()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        await schemaSettings.SelectAsync(WriteSchema("plain"));

        Assert.False(toolbar.SchemaRootPicker.IsApplicable);
    }

    [Fact]
    public async Task SwitchingToASingleSchemaFile_ClearsThePicker()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        await schemaSettings.SelectAsync(WriteOpenApiSchema("api"), rootName: "Booking");

        await schemaSettings.SelectAsync(WriteSchema("plain"));

        Assert.False(toolbar.SchemaRootPicker.IsApplicable);
        Assert.Empty(toolbar.SchemaRootPicker.Picks);
        Assert.Null(schemaSettings.SelectedRootName);
    }

    [Fact]
    public async Task PickerButton_ReadsTheBoundType()
    {
        var schemaSettings = new JsonSchemaSettings();
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        // A multi-root schema binds a root immediately rather than sitting on a placeholder.
        await schemaSettings.SelectAsync(WriteOpenApiSchema("api"));
        Assert.Equal("Booking", toolbar.SchemaRootPicker.ButtonText);

        schemaSettings.SelectRoot("Passenger");
        Assert.Equal("Passenger", toolbar.SchemaRootPicker.ButtonText);
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

        SelectSchema(toolbar, 1);
        await WaitForDocumentAsync(schemaSettings);

        Assert.Equal(entry, schemaSettings.SelectedEntry);
        Assert.NotNull(schemaSettings.Document);

        SelectSchema(toolbar, 0);

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

            SelectSchema(toolbar, toolbar.SchemaItems.Count - 1);

            Assert.Equal(new[] { JsonSchemaCatalog.GetUserDirectory() }, opened);
            Assert.Equal(0, toolbar.SelectedSchemaIndex);
            Assert.Null(schemaSettings.SelectedEntry);
        }
        finally
        {
            JsonSchemaCatalog.OpenDirectoryOverride = null;
        }
    }

    /// <summary>
    /// Regression: picking "Open schema folder…" closed the flyout from inside the ListBox's own
    /// selection commit, which tore the list out from under it and took the process down with
    /// "Index was out of range" from SelectingItemsControl.UpdateSelection. Nothing the setter
    /// does may happen before the commit that called it has finished - see UiDeferral.
    /// </summary>
    [Fact]
    public void PickingASchemaItem_TouchesNothingUntilTheInputEventHasFinished()
    {
        var opened = new List<string>();
        JsonSchemaCatalog.OpenDirectoryOverride = opened.Add;
        try
        {
            var schemaSettings = new JsonSchemaSettings();
            schemaSettings.SetEntries(new[] { WriteSchema("alpha") });
            var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
            toolbar.IsSchemaFlyoutOpen = true;

            toolbar.SelectedSchemaIndex = toolbar.SchemaItems.Count - 1;

            Assert.Empty(opened);
            Assert.True(toolbar.IsSchemaFlyoutOpen);

            ui.Pump();

            Assert.Equal(new[] { JsonSchemaCatalog.GetUserDirectory() }, opened);
            Assert.False(toolbar.IsSchemaFlyoutOpen);
        }
        finally
        {
            JsonSchemaCatalog.OpenDirectoryOverride = null;
        }
    }

    /// <summary>The same rule for the schema half of the flyout: binding a schema rebuilds
    /// SchemaItems, which is the list the commit in flight is indexing into.</summary>
    [Fact]
    public void BindingASchema_DoesNotRunInsideTheSelectionCommit()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteSchema("alpha");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });

        toolbar.SelectedSchemaIndex = 1;

        Assert.Null(schemaSettings.SelectedEntry);

        ui.Pump();

        Assert.Equal(entry, schemaSettings.SelectedEntry);
    }

    [Fact]
    public void NegativeSchemaIndex_DuringItemSwap_DoesNotClearTheBinding()
    {
        var schemaSettings = new JsonSchemaSettings();
        var entry = WriteSchema("alpha");
        schemaSettings.SetEntries(new[] { entry });
        var toolbar = new JsonToolbarViewModel(new DateHintSettings(), schemaSettings, 0, _ => { });
        SelectSchema(toolbar, 1);

        SelectSchema(toolbar, -1);

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
