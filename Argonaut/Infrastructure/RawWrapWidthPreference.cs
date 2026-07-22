using System;

namespace Argonaut.Infrastructure;

public static class RawWrapWidthPreference
{
    private const string FileName = "raw-wrap-width.json";

    /// <summary>The selectable wrap widths, ascending. Combo indices map into this array.</summary>
    public static readonly int[] Widths = [80, 160, 512];

    public const int Default = 160;

    public static int Load()
    {
        var saved = JsonSettingsStore.TryLoad<SavedWrapWidth>(FileName);
        return saved is not null ? Snap(saved.Width) : Default;
    }

    public static void Save(int width) => JsonSettingsStore.Save(FileName, new SavedWrapWidth(Snap(width)));

    /// <summary>Snaps an arbitrary saved value to the nearest selectable width, so a hand-edited
    /// or stale settings file can never produce a wrap width the combo cannot represent.</summary>
    private static int Snap(int width)
    {
        int best = Widths[0];
        foreach (int candidate in Widths)
        {
            if (Math.Abs(candidate - width) < Math.Abs(best - width))
                best = candidate;
        }

        return best;
    }

    private sealed record SavedWrapWidth(int Width);
}
