namespace GameClub.Billing;

/// <summary>
/// Asia/Tehran wall time fixed at UTC+3:30. Iran has not observed daylight
/// saving time since 2022, so the engine does not consult the OS time zone
/// database and never reads the clock. Every instant is an input.
/// </summary>
public static class TehranTime
{
    public static readonly TimeSpan Offset = new(3, 30, 0);

    public const string ZoneId = "Asia/Tehran";

    public static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0)
    {
        return new DateTimeOffset(year, month, day, hour, minute, second, Offset);
    }

    public static DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        return new DateTimeOffset(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, Offset);
    }

    public static DateTimeOffset Ensure(DateTimeOffset value)
    {
        if (value.Offset != Offset)
        {
            throw new BillingException(
                "invalid_time",
                $"Expected {ZoneId} offset +03:30 but received {value:O} (offset {value.Offset}).");
        }

        return value;
    }

    public static DateOnly Date(DateTimeOffset value)
    {
        Ensure(value);
        return DateOnly.FromDateTime(value.DateTime);
    }

    public static TimeOnly TimeOfDay(DateTimeOffset value)
    {
        Ensure(value);
        return TimeOnly.FromDateTime(value.DateTime);
    }

    /// <summary>
    /// The next strict occurrence of <paramref name="time"/> after <paramref name="cursor"/>.
    /// An instant that already sits on the mark rolls forward one day.
    /// </summary>
    public static DateTimeOffset NextWallTime(DateTimeOffset cursor, TimeOnly time)
    {
        var date = Date(cursor);
        var candidate = At(date, time);
        if (candidate <= cursor)
        {
            candidate = At(date.AddDays(1), time);
        }

        return candidate;
    }

    public static DateTimeOffset NextMidnight(DateTimeOffset cursor)
    {
        return NextWallTime(cursor, TimeOnly.MinValue);
    }

    public static long WholeSeconds(DateTimeOffset start, DateTimeOffset end)
    {
        Ensure(start);
        Ensure(end);
        var ticks = (end - start).Ticks;
        if (ticks < 0)
        {
            throw new BillingException("invalid_time", "End is before start.");
        }

        return ticks / TimeSpan.TicksPerSecond;
    }

    public static DateTimeOffset AddSeconds(DateTimeOffset value, long seconds)
    {
        Ensure(value);
        return value.AddTicks(checked(seconds * TimeSpan.TicksPerSecond));
    }
}
