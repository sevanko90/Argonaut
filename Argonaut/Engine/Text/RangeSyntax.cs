using System;
using System.Globalization;

namespace Argonaut.Engine.Text;

/// <summary>
/// How a range of lines or bytes is written - on the command line (<c>--lines 1m-2m</c>) and in a
/// filter file, which are read by the same rules so an entry means the same in both.
/// </summary>
public static class RangeSyntax
{
    /// <summary>
    /// <c>A-B</c>, <c>A-</c> (to the end, a null last), <c>A+COUNT</c>, or with
    /// <paramref name="allowSingle"/> a single <c>A</c>; each number as <see cref="ParseCount"/>
    /// reads it, none below <paramref name="lowest"/>. Null when it is not one.
    /// </summary>
    public static (long First, long? Last)? ParseRange(ReadOnlySpan<char> text, long lowest, bool allowSingle)
    {
        text = text.Trim();
        int plus = text.IndexOf('+');
        int dash = text.IndexOf('-');

        if (plus > 0)
        {
            return ParseCount(text[..plus]) is { } first && ParseCount(text[(plus + 1)..]) is { } count && first >= lowest && count >= 1
                    && count - 1 <= long.MaxValue - first
                ? (first, first + count - 1)
                : null;
        }

        if (dash > 0)
        {
            if (ParseCount(text[..dash]) is not { } first || first < lowest)
                return null;

            var rest = text[(dash + 1)..];
            if (rest.IsEmpty)
                return (first, null);

            return ParseCount(rest) is { } last && last >= first ? (first, last) : null;
        }

        return allowSingle && ParseCount(text) is { } single && single >= lowest ? (single, single) : null;
    }

    /// <summary>A whole number with an optional <c>k</c> (thousand), <c>m</c> (million) or
    /// <c>g</c> (billion) multiplier, which may follow a decimal - <c>2.5m</c> - or a hex number,
    /// <c>0x1F400</c>. Null when it is not one.</summary>
    public static long? ParseCount(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out long hex) && hex >= 0 ? hex : null;

        long multiplier = 1;
        if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase))
            multiplier = 1_000;
        else if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            multiplier = 1_000_000;
        else if (text.EndsWith("g", StringComparison.OrdinalIgnoreCase))
            multiplier = 1_000_000_000;

        if (multiplier == 1)
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long whole) ? whole : null;

        if (!decimal.TryParse(text[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal scaled))
            return null;

        decimal value = scaled * multiplier;
        return value == decimal.Truncate(value) && value <= long.MaxValue ? (long)value : null;
    }
}
