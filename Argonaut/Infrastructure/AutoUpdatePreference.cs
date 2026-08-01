namespace Argonaut.Infrastructure;

public static class AutoUpdatePreference
{
    private const string FileName = "auto-update.json";

    public static bool Load() => JsonSettingsStore.TryLoad<SavedAutoUpdate>(FileName)?.Enabled ?? true;

    public static void Save(bool enabled) => JsonSettingsStore.Save(FileName, new SavedAutoUpdate(enabled));

    private sealed record SavedAutoUpdate(bool Enabled);
}
