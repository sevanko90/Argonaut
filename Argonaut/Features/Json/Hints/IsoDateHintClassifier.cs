using System;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// Recognises an ISO 8601 date or date-time in a JSON string's raw bytes, by its fixed shape and
/// nothing else: <c>YYYY-MM-DD</c>, optionally followed by <c>T</c> or a space, <c>HH:MM</c>,
/// optional <c>:SS</c> and a fraction of up to seven digits, then optionally <c>Z</c> or an offset
/// (<c>±HH:MM</c>, <c>±HHMM</c>, <c>±HH</c>). Every field is range-checked, so "2026-02-30" is
/// not a date. Allocation-free, and it gives up at the first byte out of place.
/// </summary>
public static class IsoDateHintClassifier
{
    public static bool TryClassify(ReadOnlySpan<byte> text, out long ticks, out IsoDateShape shape, out short offsetMinutes)
    {
        ticks = 0;
        shape = IsoDateShape.Date;
        offsetMinutes = 0;

        if (text.Length < 10 || !Digits(text, 0, 4, out int year) || text[4] != '-' || !Digits(text, 5, 2, out int month)
            || text[7] != '-' || !Digits(text, 8, 2, out int day))
            return false;

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        long dayTicks = new DateTime(year, month, day).Ticks;
        if (text.Length == 10)
        {
            ticks = dayTicks;
            return true;
        }

        if (text.Length < 16 || text[10] is not ((byte)'T' or (byte)' ') || !Digits(text, 11, 2, out int hour) || text[13] != ':'
            || !Digits(text, 14, 2, out int minute) || hour > 23 || minute > 59)
            return false;

        long time = hour * TimeSpan.TicksPerHour + minute * TimeSpan.TicksPerMinute;
        int at = 16;
        if (at < text.Length && text[at] == ':')
        {
            if (!Digits(text, at + 1, 2, out int second) || second > 59)
                return false;

            time += second * TimeSpan.TicksPerSecond;
            at += 3;
            if (at < text.Length && text[at] is (byte)'.' or (byte)',')
            {
                int start = ++at;
                long fraction = 0;
                while (at < text.Length && at - start < 7 && IsDigit(text[at]))
                    fraction = fraction * 10 + (text[at++] - '0');
                if (at == start || (at < text.Length && IsDigit(text[at])))
                    return false;

                for (int place = at - start; place < 7; place++)
                    fraction *= 10;
                time += fraction;
            }
        }

        if (at == text.Length)
        {
            ticks = dayTicks + time;
            shape = IsoDateShape.LocalDateTime;
            return true;
        }

        int offset;
        if (text[at] == 'Z' && at + 1 == text.Length)
        {
            offset = 0;
        }
        else if (text[at] is (byte)'+' or (byte)'-' && TryOffset(text[(at + 1)..], out offset))
        {
            if (text[at] == '-')
                offset = -offset;
        }
        else
        {
            return false;
        }

        long utc = dayTicks + time - offset * TimeSpan.TicksPerMinute;
        if (utc < DateTime.MinValue.Ticks || utc > DateTime.MaxValue.Ticks)
            return false;

        ticks = utc;
        shape = IsoDateShape.Instant;
        offsetMinutes = (short)offset;
        return true;
    }

    /// <summary>An offset after its sign: <c>HH</c>, <c>HHMM</c> or <c>HH:MM</c>, up to 14 hours.</summary>
    private static bool TryOffset(ReadOnlySpan<byte> text, out int minutes)
    {
        minutes = 0;
        int minuteAt = text.Length switch { 2 => -1, 4 => 2, 5 when text[2] == ':' => 3, _ => -2 };
        if (minuteAt == -2 || !Digits(text, 0, 2, out int hours))
            return false;

        int extra = 0;
        if (minuteAt > 0 && (!Digits(text, minuteAt, 2, out extra) || extra > 59))
            return false;

        minutes = hours * 60 + extra;
        return minutes <= 14 * 60;
    }

    private static bool Digits(ReadOnlySpan<byte> text, int start, int count, out int number)
    {
        number = 0;
        if (start + count > text.Length)
            return false;

        for (int i = start; i < start + count; i++)
        {
            if (!IsDigit(text[i]))
                return false;
            number = number * 10 + (text[i] - '0');
        }

        return true;
    }

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
