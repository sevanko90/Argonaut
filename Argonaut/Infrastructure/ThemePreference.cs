using System;

namespace Argonaut.Infrastructure;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public static class ThemePreference
{
    private const string FileName = "theme.json";

    public static ThemeMode Load()
    {
        var saved = JsonSettingsStore.TryLoad<SavedTheme>(FileName);
        return saved is not null && Enum.TryParse<ThemeMode>(saved.Mode, out var mode) ? mode : ThemeMode.System;
    }

    public static void Save(ThemeMode mode) => JsonSettingsStore.Save(FileName, new SavedTheme(mode.ToString()));

    private sealed record SavedTheme(string Mode);
}
