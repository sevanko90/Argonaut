using Argonaut.Features.Json;
using Argonaut.Features.Json.Hints;
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

        var toolbar = new JsonToolbarViewModel(settings, initialExpandDepthIndex: 3, applyExpandDepth: _ => { });

        Assert.Equal((int)DateDecodingScheme.JsSeconds, toolbar.DateHintSchemeIndex);
        Assert.Equal((int)DateHintTimeZoneMode.Utc, toolbar.TimeZoneModeIndex);
        Assert.Equal(3, toolbar.ExpandDepthIndex);
    }

    [Fact]
    public void DateHintSchemeIndex_Set_UpdatesSettings_AndLatchesUserSelected()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, 0, _ => { });

        toolbar.DateHintSchemeIndex = (int)DateDecodingScheme.KeepaMinutes;

        Assert.Equal(DateDecodingScheme.KeepaMinutes, settings.FileDefaultScheme);
        Assert.True(settings.IsUserSelected);
    }

    [Fact]
    public void TimeZoneModeIndex_Set_UpdatesSettings()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, 0, _ => { });

        toolbar.TimeZoneModeIndex = (int)DateHintTimeZoneMode.Utc;

        Assert.Equal(DateHintTimeZoneMode.Utc, settings.TimeZoneMode);
    }

    [Fact]
    public void NegativeIndexAssignments_AreIgnored()
    {
        var settings = new DateHintSettings();
        var toolbar = new JsonToolbarViewModel(settings, 0, _ => { });

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
        var toolbar = new JsonToolbarViewModel(settings, 0, _ => { });

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

    [Fact]
    public void ExpandDepthIndex_Set_PersistsAndInvokesCallback()
    {
        var settings = new DateHintSettings();
        var applied = new List<int>();
        var toolbar = new JsonToolbarViewModel(settings, 0, applied.Add);

        toolbar.ExpandDepthIndex = 4;

        Assert.Equal(4, ExpandDepthPreference.Load());
        Assert.Equal(new[] { 4 }, applied);

        toolbar.ExpandDepthIndex = 4;

        Assert.Equal(new[] { 4 }, applied);
    }
}
