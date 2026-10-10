using System;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// How far a time is from now, as a person would say it: "just now", "in under a minute",
/// "5 min ago", "in 3 hours",
/// "yesterday", "in 4 months", "2 years ago". Rounded to the largest unit that reads naturally,
/// so 40 days is "1 month" and 11 months is "1 year".
/// </summary>
public static class RelativeTime
{
    /// <summary>From a time of day, <paramref name="delta"/> being that time minus now.</summary>
    public static string Describe(TimeSpan delta)
    {
        double seconds = Math.Abs(delta.TotalSeconds);
        if (seconds < 45)
            return delta < TimeSpan.Zero ? "just now" : "in under a minute";

        string amount = seconds switch
        {
            < 45 * 60 => Count(Math.Max(1, Math.Round(seconds / 60)), "min", "min"),
            < 22 * 3600 => Count(Math.Max(1, Math.Round(seconds / 3600)), "hour", "hours"),
            _ => Days(seconds / 86400),
        };
        return delta < TimeSpan.Zero ? $"{amount} ago" : $"in {amount}";
    }

    /// <summary>From a calendar day, <paramref name="days"/> being that day minus today.</summary>
    public static string DescribeDays(int days) => days switch
    {
        0 => "today",
        -1 => "yesterday",
        1 => "tomorrow",
        < 0 => $"{Days(-days)} ago",
        _ => $"in {Days(days)}",
    };

    private static string Days(double days) => days switch
    {
        < 26 => Count(Math.Max(1, Math.Round(days)), "day", "days"),
        < 320 => Count(Math.Max(1, Math.Round(days / 30.44)), "month", "months"),
        _ => Count(Math.Max(1, Math.Round(days / 365.25)), "year", "years"),
    };

    private static string Count(double count, string one, string many) => count == 1 ? $"1 {one}" : $"{count:0} {many}";
}
