using System;
using System.Globalization;

namespace Argonaut.Engine.Text;

/// <summary>
/// A count short enough for a label: exact up to 9,999, then three significant figures with a
/// <c>k</c>, <c>m</c> or <c>g</c> multiplier - <c>100k</c>, <c>1.5m</c>, <c>2.45g</c>. The same
/// multipliers the command line reads (<c>--lines 1m-2m</c>), so a label shows a range in the
/// form it was asked for. Rounded, so anything that must be exact shows the full number too.
///
/// A range is rounded as a pair (<see cref="FormatRange"/>), so its two ends never read the same
/// unless they are: <c>4g–4.0002g</c>, not <c>4g–4g</c>.
/// </summary>
public static class CompactCount
{
    private const long ExactBelow = 10_000;

    /// <summary>Figures a single count is rounded to.</summary>
    private const int DefaultFigures = 3;

    /// <summary>The most figures a range's ends are given to tell them apart; ends closer than
    /// that are shown in full, which is shorter than a multiplier with seven figures.</summary>
    private const int MaxFigures = 6;

    private static readonly (double Scale, string Suffix)[] Multipliers =
    [
        (1e3, "k"),
        (1e6, "m"),
        (1e9, "g"),
    ];

    public static string Format(long count) => Format(count, DefaultFigures);

    /// <summary>
    /// The two ends of a range, rounded alike: to three figures, or as many more as it takes for
    /// two different ends to read differently - <c>4g–4.0002g</c>. Ends that would still read
    /// the same at <see cref="MaxFigures"/> are both written in full.
    /// </summary>
    public static (string First, string Last) FormatRange(long first, long last)
    {
        for (int figures = DefaultFigures; figures <= MaxFigures; figures++)
        {
            string a = Format(first, figures);
            string b = Format(last, figures);
            if (first == last || a != b)
                return (a, b);
        }

        return (Exact(first), Exact(last));
    }

    private static string Format(long count, int figures)
    {
        if (count < ExactBelow)
            return Exact(count);

        for (int i = 0; i < Multipliers.Length; i++)
        {
            var (scale, suffix) = Multipliers[i];
            double scaled = count / scale;

            // Anything that would show as 1000 or more is the next multiplier's - including
            // 999,999, which rounds to "1000k" and is "1m".
            if (scaled >= 999.5 && i < Multipliers.Length - 1)
                continue;

            int wholeDigits = scaled < 10 ? 1 : scaled < 100 ? 2 : 3;
            int decimals = Math.Max(0, figures - wholeDigits);
            string format = decimals == 0 ? "0" : "0." + new string('#', decimals);
            return scaled.ToString(format, CultureInfo.InvariantCulture) + suffix;
        }

        return Exact(count);
    }

    private static string Exact(long count) => count.ToString("N0", CultureInfo.CurrentCulture);
}
