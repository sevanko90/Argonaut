using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Header toolbar for the JSON tree view: date-hint scheme/time-zone radio groups (behind a
/// single "Date" dropdown button) bound to a document's <see cref="DateHintSettings"/>, the
/// default-expand-depth combo, and (JSON documents only) a "jump to JSONPath" text entry.
/// Shared by JsonViewModel and NdJsonViewModel, which expose an identical surface (a
/// DateHintSettings instance and a SetDefaultExpandDepth callback) and previously drove these
/// same combos through the shell via type-switches. NdJsonViewModel omits
/// <paramref name="navigateToPath"/> (path navigation isn't a per-line NDJSON concept), which
/// hides the path entry via <see cref="SupportsPathNavigation"/>.
///
/// Owned by the document view model that creates it (see <see cref="JsonViewModel.Toolbar"/> /
/// NdJsonViewModel's equivalent) and shares its lifetime - no unsubscription is needed since
/// this and the settings object it subscribes to are disposed together.
/// </summary>
public sealed class JsonToolbarViewModel : ObservableObject
{
    private readonly DateHintSettings settings;
    private readonly Action<int> applyExpandDepth;
    private readonly Func<string, Task>? navigateToPath;
    private int dateHintSchemeIndex;
    private int timeZoneModeIndex;
    private int expandDepthIndex;
    private string jsonPathInput = string.Empty;

    public JsonToolbarViewModel(DateHintSettings settings, int initialExpandDepthIndex, Action<int> applyExpandDepth, Func<string, Task>? navigateToPath = null)
    {
        this.settings = settings;
        this.applyExpandDepth = applyExpandDepth;
        this.navigateToPath = navigateToPath;

        dateHintSchemeIndex = (int)settings.FileDefaultScheme;
        timeZoneModeIndex = (int)settings.TimeZoneMode;
        expandDepthIndex = initialExpandDepthIndex;

        settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>Whether the "jump to JSONPath" text entry should be shown - false for the
    /// shared NDJSON toolbar, which has no single-document JSONPath concept.</summary>
    public bool SupportsPathNavigation => navigateToPath is not null;

    /// <summary>Bound two-way to the "jump to path" text entry.</summary>
    public string JsonPathInput
    {
        get => jsonPathInput;
        set => SetField(ref jsonPathInput, value);
    }

    /// <summary>Resolves <see cref="JsonPathInput"/> and navigates to it; no-op if path
    /// navigation isn't supported or the box is blank.</summary>
    public Task GoToPathAsync()
    {
        if (navigateToPath is null || string.IsNullOrWhiteSpace(jsonPathInput))
            return Task.CompletedTask;

        return navigateToPath(jsonPathInput);
    }

    /// <summary>Bound two-way to the date-hint scheme combo; forwards to <see cref="DateHintSettings"/>.</summary>
    public int DateHintSchemeIndex
    {
        get => dateHintSchemeIndex;
        set
        {
            if (value < 0 || !SetField(ref dateHintSchemeIndex, value))
                return;

            settings.SetUserDefault((DateDecodingScheme)value);
        }
    }

    /// <summary>Bound two-way to the time-zone combo; forwards to <see cref="DateHintSettings"/>.</summary>
    public int TimeZoneModeIndex
    {
        get => timeZoneModeIndex;
        set
        {
            if (value < 0 || !SetField(ref timeZoneModeIndex, value))
                return;

            settings.SetTimeZoneMode((DateHintTimeZoneMode)value);
        }
    }

    /// <summary>Bound two-way to the expand-depth combo. Persists the choice and applies it
    /// live to the owning document's tree.</summary>
    public int ExpandDepthIndex
    {
        get => expandDepthIndex;
        set
        {
            if (value < 0 || !SetField(ref expandDepthIndex, value))
                return;

            ExpandDepthPreference.Save(value);
            applyExpandDepth(value);
        }
    }

    /// <summary>Inference completing in the background updates FileDefaultScheme - reflect it
    /// live in the combo.</summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(DateHintSettings.FileDefaultScheme) or nameof(DateHintSettings.TimeZoneMode))
            SyncFromSettings();
    }

    /// <summary>
    /// Pushes the current settings values into the bound combo indices. SetField's equality
    /// check makes this a no-op when nothing changed, so the resulting property notification
    /// doesn't loop back through the combo setters into <see cref="DateHintSettings"/>.
    /// </summary>
    private void SyncFromSettings()
    {
        SetField(ref dateHintSchemeIndex, (int)settings.FileDefaultScheme, nameof(DateHintSchemeIndex));
        SetField(ref timeZoneModeIndex, (int)settings.TimeZoneMode, nameof(TimeZoneModeIndex));
    }
}
