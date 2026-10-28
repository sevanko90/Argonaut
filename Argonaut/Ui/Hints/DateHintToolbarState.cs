using System.ComponentModel;
using Argonaut.Engine.Hints;
using Argonaut.Ui.ViewModels;

namespace Argonaut.Ui.Hints;

/// <summary>
/// The state behind <see cref="DateHintToolbarControls"/>: the date-hint scheme and time-zone
/// choices, two-way bound to a document's <see cref="DateHintSettings"/>. A toolbar view model
/// owns one and hosts the controls on it. It lives and dies with the settings it subscribes to,
/// so no unsubscription is needed.
/// </summary>
public sealed class DateHintToolbarState : ObservableObject
{
    private readonly DateHintSettings settings;
    private int dateHintSchemeIndex;
    private int timeZoneModeIndex;

    public DateHintToolbarState(DateHintSettings settings)
    {
        this.settings = settings;
        dateHintSchemeIndex = (int)settings.FileDefaultScheme;
        timeZoneModeIndex = (int)settings.TimeZoneMode;
        settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    /// <summary>Bound two-way to the date-hint scheme choices; forwards to <see cref="DateHintSettings"/>.</summary>
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

    /// <summary>Bound two-way to the time-zone choices; forwards to <see cref="DateHintSettings"/>.</summary>
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

    /// <summary>Inference completing in the background updates FileDefaultScheme - reflect it
    /// live in the choices.</summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(DateHintSettings.FileDefaultScheme) or nameof(DateHintSettings.TimeZoneMode))
            SyncFromSettings();
    }

    /// <summary>Pushes the current settings values into the bound indices. SetField's equality
    /// check makes this a no-op when nothing changed, so the notification does not loop back
    /// through the setters into <see cref="DateHintSettings"/>.</summary>
    private void SyncFromSettings()
    {
        SetField(ref dateHintSchemeIndex, (int)settings.FileDefaultScheme, nameof(DateHintSchemeIndex));
        SetField(ref timeZoneModeIndex, (int)settings.TimeZoneMode, nameof(TimeZoneModeIndex));
    }
}
