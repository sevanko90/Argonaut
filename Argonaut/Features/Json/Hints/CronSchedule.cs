using System;

namespace Argonaut.Features.Json.Hints;

/// <summary>
/// A five-field cron schedule as the set of values each field allows, one bit per value: minutes
/// 0-59, hours 0-23, days of the month 1-31, months 1-12, days of the week 0-6 from Sunday. Day of
/// month and day of week follow Vixie cron: when both are restricted, a day matching either runs.
/// </summary>
public readonly record struct CronSchedule(ulong Minutes, uint Hours, uint Days, ushort Months, byte Weekdays,
    bool AnyDay, bool AnyWeekday)
{
    /// <summary>The first minute after <paramref name="after"/> the schedule runs, or null when it
    /// never does within four years (31 February, say).</summary>
    public DateTime? NextAfter(DateTime after)
    {
        var start = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var day = start.Date;
        for (int d = 0; d < 366 * 4; d++, day = day.AddDays(1))
        {
            if ((Months & (1 << day.Month)) == 0 || !RunsOn(day))
                continue;

            int firstHour = day == start.Date ? start.Hour : 0;
            for (int hour = firstHour; hour < 24; hour++)
            {
                if ((Hours & (1u << hour)) == 0)
                    continue;

                int firstMinute = day == start.Date && hour == start.Hour ? start.Minute : 0;
                for (int minute = firstMinute; minute < 60; minute++)
                {
                    if ((Minutes & (1UL << minute)) != 0)
                        return day.AddHours(hour).AddMinutes(minute);
                }
            }
        }

        return null;
    }

    private bool RunsOn(DateTime day)
    {
        bool dayMatches = (Days & (1u << day.Day)) != 0;
        bool weekdayMatches = (Weekdays & (1 << (int)day.DayOfWeek)) != 0;
        return AnyDay ? weekdayMatches : AnyWeekday ? dayMatches : dayMatches || weekdayMatches;
    }
}
