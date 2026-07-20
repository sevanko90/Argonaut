namespace Argonaut.Infrastructure;

public static class ExpandDepthPreference
{
    private const string FileName = "expand-depth.json";
    public const int Default = 2;
    public const int Min = 0;
    public const int Max = 5;

    public static int Load()
    {
        var saved = JsonSettingsStore.TryLoad<SavedExpandDepth>(FileName);
        return saved is not null ? Clamp(saved.Depth) : Default;
    }

    public static void Save(int depth) => JsonSettingsStore.Save(FileName, new SavedExpandDepth(Clamp(depth)));

    private static int Clamp(int depth) => depth < Min ? Min : depth > Max ? Max : depth;

    private sealed record SavedExpandDepth(int Depth);
}
