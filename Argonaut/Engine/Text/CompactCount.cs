using System.Globalization;

namespace Argonaut.Engine.Text;

/// <summary>
/// A count short enough for a label: exact up to 9,999, then three significant figures with a
/// <c>k</c>, <c>m</c> or <c>g</c> multiplier - <c>100k</c>, <c>1.5m</c>, <c>2.45g</c>. The same
/// multipliers the command line reads (<c>--lines 1m-2m</c>), so a label shows a range in the
/// form it was asked for. Rounded, so anything that must be exact shows the full number too.
/// </summary>
public static class CompactCount
{
    private const long ExactBelow = 10_000;

    private static readonly (double Scale, string Suffix)[] Multipliers =
    [
        (1e3, "k"),
        (1e6, "m"),
        (1e9, "g"),
    ];

    public static string Format(long count)
    {
        if (count < ExactBelow)
            return count.ToString("N0", CultureInfo.CurrentCulture);

        for (int i = 0; i < Multipliers.Length; i++)
        {
            var (scale, suffix) = Multipliers[i];
            double scaled = count / scale;

            // Anything that would show as 1000 or more is the next multiplier's - including
            // 999,999, which rounds to "1000k" and is "1m".
            if (scaled >= 999.5 && i < Multipliers.Length - 1)
                continue;

            return scaled.ToString(scaled < 10 ? "0.##" : scaled < 100 ? "0.#" : "0", CultureInfo.InvariantCulture) + suffix;
        }

        return count.ToString("N0", CultureInfo.CurrentCulture);
    }
}
