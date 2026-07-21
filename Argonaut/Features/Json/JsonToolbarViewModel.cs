using System;
using System.ComponentModel;
using Argonaut.Features.Json.Hints;
using Argonaut.Infrastructure;

namespace Argonaut.Features.Json;

/// <summary>
/// Header toolbar for the JSON tree view: date-hint scheme/time-zone combos bound to a
/// document's <see cref="DateHintSettings"/>, plus the default-expand-depth combo. Shared by
/// JsonViewModel and NdJsonViewModel, which expose an identical surface (a DateHintSettings
/// instance and a SetDefaultExpandDepth callback) and previously drove these same three combos
/// through the shell via type-switches.
///
/// Owned by the document view model that creates it (see <see cref="JsonViewModel.Toolbar"/> /
/// NdJsonViewModel's equivalent) and shares its lifetime - no unsubscription is needed since
/// this and the settings object it subscribes to are disposed together.
/// </summary>
public sealed class JsonToolbarViewModel : ObservableObject
{
    private readonly DateHintSettings settings;
    private readonly Action<int> applyExpandDepth;
    private int dateHintSchemeIndex;
    private int timeZoneModeIndex;
    private int expandDepthIndex;

    public JsonToolbarViewModel(DateHintSettings settings, int initialExpandDepthIndex, Action<int> applyExpandDepth)
    {
        this.settings = settings;
        this.applyExpandDepth = applyExpandDepth;

        dateHintSchemeIndex = (int)settings.FileDefaultScheme;
        timeZoneModeIndex = (int)settings.TimeZoneMode;
        expandDepthIndex = initialExpandDepthIndex;

        settings.PropertyChanged += OnSettingsPropertyChanged;
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
