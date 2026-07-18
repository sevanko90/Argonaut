using System;
using System.Globalization;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Converts a classified numeric value to a unix-milliseconds timestamp under a given scheme,
/// and formats it in local time. Never throws: out-of-range conversions (possible when a
/// per-token override applies a scheme the value's digit length wasn't meant for) return false.
/// </summary>
public static class DateHintDecoder
{
    public const long KeepaEpochOffsetMinutes = 21_564_000;

    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();
    private static readonly long MinUnixMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds();

    public static bool TryToUnixMilliseconds(long value, DateDecodingScheme scheme, out long unixMs)
    {
        unixMs = 0;

        switch (scheme)
        {
            case DateDecodingScheme.JsMilliseconds:
                unixMs = value;
                break;

            case DateDecodingScheme.JsSeconds:
                if (value > long.MaxValue / 1000 || value < long.MinValue / 1000)
                    return false;
                unixMs = value * 1000;
                break;

            case DateDecodingScheme.KeepaMinutes:
                long keepaTotalMinutes = value + KeepaEpochOffsetMinutes;
                if (keepaTotalMinutes > long.MaxValue / 60_000 || keepaTotalMinutes < long.MinValue / 60_000)
                    return false;
                unixMs = keepaTotalMinutes * 60_000;
                break;

            default:
                return false;
        }

        return unixMs >= MinUnixMs && unixMs <= MaxUnixMs;
    }

    public static string? Format(long value, DateDecodingScheme scheme, DateHintTimeZoneMode mode)
    {
        if (!TryToUnixMilliseconds(value, scheme, out long unixMs))
            return null;

        return mode == DateHintTimeZoneMode.Utc ? FormatUtc(unixMs) : FormatLocal(unixMs);
    }

    private static string FormatLocal(long unixMs)
    {
        var local = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime();

        // "Local" means the machine's configured date/time format, not a fixed ISO-ish
        // pattern: short date pattern + long time pattern (with seconds) from the current
        // culture, so a US machine shows 12-hour "6/15/2024 2:05:09 PM" while a UK machine
        // shows 24-hour "15/06/2024 14:05:09".
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        string timestamp = local.ToString($"{format.ShortDatePattern} {format.LongTimePattern}", CultureInfo.CurrentCulture);

        // The UTC offset is resolved for the decoded instant itself (correctly DST-aware for
        // that date), not for "now" - e.g. a December date shows +00:00 even if the viewer is
        // currently in BST. Shown explicitly so that isn't mistaken for a conversion bug.
        return $"{timestamp} [local, UTC{FormatOffset(local.Offset)}]";
    }

    private static string FormatUtc(long unixMs)
    {
        // Display-friendly ISO-ish shape, not the locale format - UTC is a fixed,
        // unambiguous point of reference, so it's rendered the same way regardless of the
        // viewer's machine settings.
        string timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return $"{timestamp} [UTC]";
    }

    private static string FormatOffset(TimeSpan offset)
    {
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        var magnitude = offset.Duration();
        return magnitude.Minutes == 0
            ? $"{sign}{magnitude.Hours}"
            : $"{sign}{magnitude.Hours}:{magnitude.Minutes:D2}";
    }
}
