using System;

namespace Argonaut.Infrastructure;

public enum ContentFontMode
{
    Monospace,
    SansSerif
}

/// <summary>Persists the content-font toggle (monospace vs sans-serif in the document views).</summary>
public static class ContentFontPreference
{
    private const string FileName = "font.json";

    public static ContentFontMode Load()
    {
        var saved = JsonSettingsStore.TryLoad<SavedFont>(FileName);
        return saved is not null && Enum.TryParse<ContentFontMode>(saved.Mode, out var mode) ? mode : ContentFontMode.Monospace;
    }

    public static void Save(ContentFontMode mode) => JsonSettingsStore.Save(FileName, new SavedFont(mode.ToString()));

    private sealed record SavedFont(string Mode);
}
