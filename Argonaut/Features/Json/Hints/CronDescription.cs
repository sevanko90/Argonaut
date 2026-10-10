using System;
using System.Collections.Generic;
using System.Text;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A cron schedule in words: "every 15 min", "at 09:00, Mon–Fri", "at 02:30 on day 1",
/// "hourly at :15". Read from what each field allows rather than how it was written, so
/// <c>*/15</c> and <c>0,15,30,45</c> read the same.
/// </summary>
public static class CronDescription
{
    private static readonly string[] Months = ["", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

    public static string Describe(in CronSchedule schedule)
    {
        var text = new StringBuilder(Time(schedule.Minutes, schedule.Hours));

        if (!schedule.AnyWeekday && schedule.Weekdays != 0x7F)
            text.Append(", ").Append(Values(schedule.Weekdays, 0, 6, v => Weekdays[v], every: "days"));
        if (!schedule.AnyDay && schedule.Days != 0xFFFFFFFE)
            text.Append(schedule.AnyWeekday ? " on day" : " or day").Append(Plural(schedule.Days)).Append(' ').Append(Values(schedule.Days, 1, 31, v => v.ToString(), every: "days"));
        if (schedule.Months != 0x1FFE)
            text.Append(" in ").Append(Values(schedule.Months, 1, 12, v => Months[v], every: "months"));

        return text.ToString();
    }

    private static string Time(ulong minutes, uint hours)
    {
        bool everyMinute = minutes == (1UL << 60) - 1, everyHour = hours == (1u << 24) - 1;
        if (everyMinute && everyHour)
            return "every minute";
        if (Step(minutes, 60) is { } minuteStep && everyHour)
            return $"every {minuteStep} min";
        if (Single(minutes) is { } minute)
        {
            if (everyHour)
                return $"hourly at :{minute:D2}";
            if (Single(hours) is { } hour)
                return $"at {hour:D2}:{minute:D2}";
            if (Step(hours, 24) is { } hourStep)
                return $"at :{minute:D2} every {hourStep} hours";
            if (BitCount(hours) <= 4)
                return "at " + string.Join(", ", Set(hours).ConvertAll(h => $"{h:D2}:{minute:D2}"));
            return $"at :{minute:D2}, hours {Values(hours, 0, 23, h => $"{h:D2}", every: "hours")}";
        }

        string minutePart = everyMinute ? "every minute" : $"minutes {Values(minutes, 0, 59, m => m.ToString(), every: "minutes")}";
        return everyHour ? minutePart : $"{minutePart}, hours {Values(hours, 0, 23, h => $"{h:D2}", every: "hours")}";
    }

    /// <summary>A field's values as a step ("every 2 hours"), runs ("Mon–Fri") and singles.</summary>
    private static string Values(ulong mask, int min, int max, Func<int, string> name, string every)
    {
        if (Step(mask >> min << min, max + 1, min) is { } step)
            return $"every {step} {every}";

        var parts = new List<string>();
        var values = Set(mask);
        for (int i = 0; i < values.Count;)
        {
            int j = i;
            while (j + 1 < values.Count && values[j + 1] == values[j] + 1)
                j++;
            parts.Add(j - i >= 2 ? $"{name(values[i])}–{name(values[j])}" : name(values[i]));
            if (j - i == 1)
                parts.Add(name(values[j]));
            i = j + 1;
        }

        return string.Join(", ", parts);
    }

    /// <summary>The step n when the field is every n-th value from its first, with more than one
    /// value; otherwise null.</summary>
    private static int? Step(ulong mask, int end, int start = 0)
    {
        var values = Set(mask);
        if (values.Count < 2 || values[0] != start)
            return null;

        int step = values[1] - values[0];
        for (int i = 1; i < values.Count; i++)
        {
            if (values[i] - values[i - 1] != step)
                return null;
        }

        return values[^1] + step >= end ? step : null;
    }

    private static int? Single(ulong mask) => BitCount(mask) == 1 ? Set(mask)[0] : null;

    private static string Plural(ulong mask) => BitCount(mask) == 1 ? "" : "s";

    private static int BitCount(ulong mask) => System.Numerics.BitOperations.PopCount(mask);

    private static List<int> Set(ulong mask)
    {
        var values = new List<int>();
        for (int bit = 0; bit < 64; bit++)
        {
            if ((mask & (1UL << bit)) != 0)
                values.Add(bit);
        }

        return values;
    }
}
