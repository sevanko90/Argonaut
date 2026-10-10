using System;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Recognises a cron expression: five space-separated fields - minute, hour, day of month, month,
/// day of week - each <c>*</c>, a number, a range <c>a-b</c>, a step <c>*/n</c> or <c>a-b/n</c>, or a
/// comma list of those, with month and day names (<c>JAN</c>, <c>MON</c>) where cron allows them;
/// or one of the macros <c>@yearly</c>, <c>@monthly</c>, <c>@weekly</c>, <c>@daily</c>,
/// <c>@hourly</c>. Five plain numbers are valid cron but far more often something else, so a
/// schedule must use at least one of <c>* / - ,</c> or a name. Allocation-free.
/// </summary>
public static class CronHintClassifier
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] WeekdayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    public static bool TryParse(ReadOnlySpan<byte> text, out CronSchedule schedule)
    {
        schedule = default;
        if (text.Length > 0 && text[0] == '@')
            return TryMacro(text, out schedule);

        Span<Range> fields = stackalloc Range[6];
        if (Split(text, fields) != 5)
            return false;

        bool hasSyntax = false;
        if (!TryField(text[fields[0]], 0, 59, null, ref hasSyntax, out ulong minutes, out _)
            || !TryField(text[fields[1]], 0, 23, null, ref hasSyntax, out ulong hours, out _)
            || !TryField(text[fields[2]], 1, 31, null, ref hasSyntax, out ulong days, out bool anyDay)
            || !TryField(text[fields[3]], 1, 12, MonthNames, ref hasSyntax, out ulong months, out _)
            || !TryField(text[fields[4]], 0, 7, WeekdayNames, ref hasSyntax, out ulong weekdays, out bool anyWeekday)
            || !hasSyntax)
            return false;

        // 7 is Sunday as well as 0.
        if ((weekdays & (1UL << 7)) != 0)
            weekdays = (weekdays | 1) & 0x7F;

        schedule = new CronSchedule(minutes, (uint)hours, (uint)days, (ushort)months, (byte)weekdays, anyDay, anyWeekday);
        return true;
    }

    private static bool TryMacro(ReadOnlySpan<byte> text, out CronSchedule schedule)
    {
        const ulong everyDay = 0xFFFFFFFE; // days 1-31
        const ushort everyMonth = 0x1FFE;  // months 1-12
        schedule = text switch
        {
            _ when text.SequenceEqual("@yearly"u8) || text.SequenceEqual("@annually"u8) => new CronSchedule(1, 1, 1u << 1, 1 << 1, 0x7F, false, true),
            _ when text.SequenceEqual("@monthly"u8) => new CronSchedule(1, 1, 1u << 1, everyMonth, 0x7F, false, true),
            _ when text.SequenceEqual("@weekly"u8) => new CronSchedule(1, 1, (uint)everyDay, everyMonth, 1, true, false),
            _ when text.SequenceEqual("@daily"u8) || text.SequenceEqual("@midnight"u8) => new CronSchedule(1, 1, (uint)everyDay, everyMonth, 0x7F, true, true),
            _ when text.SequenceEqual("@hourly"u8) => new CronSchedule(1, 0xFFFFFF, (uint)everyDay, everyMonth, 0x7F, true, true),
            _ => default,
        };
        return schedule.Hours != 0;
    }

    /// <summary>Splits on single spaces into at most <paramref name="fields"/>.Length fields;
    /// returns how many, or -1 for an empty field (a doubled or edge space).</summary>
    private static int Split(ReadOnlySpan<byte> text, Span<Range> fields)
    {
        int count = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != ' ')
                continue;
            if (i == start || count == fields.Length)
                return -1;
            fields[count++] = new Range(start, i);
            start = i + 1;
        }

        return count;
    }

    private static bool TryField(ReadOnlySpan<byte> field, int min, int max, string[]? names, ref bool hasSyntax, out ulong mask, out bool any)
    {
        mask = 0;
        any = field.Length == 1 && field[0] == '*';
        while (!field.IsEmpty)
        {
            int comma = field.IndexOf((byte)',');
            var part = comma < 0 ? field : field[..comma];
            field = comma < 0 ? default : field[(comma + 1)..];
            if (comma >= 0)
            {
                hasSyntax = true;
                if (field.IsEmpty)
                    return false;
            }

            int step = 1;
            int slash = part.IndexOf((byte)'/');
            if (slash >= 0)
            {
                hasSyntax = true;
                if (!TryNumber(part[(slash + 1)..], out step) || step < 1)
                    return false;
                part = part[..slash];
            }

            int low, high;
            if (part.Length == 1 && part[0] == '*')
            {
                hasSyntax = true;
                (low, high) = (min, max == 7 ? 6 : max);
            }
            else
            {
                int dash = part.IndexOf((byte)'-');
                if (dash >= 0)
                    hasSyntax = true;
                if (!TryValue(dash < 0 ? part : part[..dash], min, names, ref hasSyntax, out low)
                    || !TryValue(dash < 0 ? part : part[(dash + 1)..], min, names, ref hasSyntax, out high))
                    return false;
                if (slash >= 0 && dash < 0)
                    high = max; // "5/15" runs from 5 on
            }

            if (low < min || high > max || low > high)
                return false;
            for (int value = low; value <= high; value += step)
                mask |= 1UL << value;
        }

        return mask != 0;
    }

    private static bool TryValue(ReadOnlySpan<byte> text, int min, string[]? names, ref bool hasSyntax, out int value)
    {
        if (TryNumber(text, out value))
            return true;

        if (names is not null && text.Length == 3)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if ((text[0] | 0x20) == (names[i][0] | 0x20) && (text[1] | 0x20) == (names[i][1] | 0x20) && (text[2] | 0x20) == (names[i][2] | 0x20))
                {
                    hasSyntax = true;
                    value = i + min;
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryNumber(ReadOnlySpan<byte> text, out int value)
    {
        value = 0;
        if (text.IsEmpty || text.Length > 2)
            return false;
        foreach (byte b in text)
        {
            if (b is < (byte)'0' or > (byte)'9')
                return false;
            value = value * 10 + (b - '0');
        }

        return true;
    }
}
