using System;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Argonaut.Engine.Settings;

namespace Argonaut.Shell;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>Monospace vs sans-serif in the document views.</summary>
public enum ContentFontMode
{
    Monospace,
    SansSerif
}

/// <summary>The window's remembered theme and content font, toggled from the status bar.</summary>
public sealed class AppearanceSettings : ISettingsBlock<AppearanceSettings>
{
    public static string Key => "appearance";

    public static JsonTypeInfo<AppearanceSettings> JsonTypeInfo => AppearanceSettingsJson.Default.AppearanceSettings;

    // The enum converter still accepts a bare number, which can name no member at all.
    public ThemeMode Theme
    {
        get;
        set => field = Enum.IsDefined(value) ? value : ThemeMode.System;
    }

    public ContentFontMode ContentFont
    {
        get;
        set => field = Enum.IsDefined(value) ? value : ContentFontMode.Monospace;
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppearanceSettings))]
internal sealed partial class AppearanceSettingsJson : JsonSerializerContext;
