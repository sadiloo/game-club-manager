namespace GameClub.Billing;

/// <summary>
/// Asia/Tehran wall time fixed at UTC+3:30. Iran has not observed daylight
/// saving time since 2022, so the engine does not consult the OS time zone
/// database and never reads the clock. Every instant is an input.
/// An instant with any other offset is the same moment rewritten at +03:30.
/// Windows 7 still reports Iran as +04:30; that offset is normalized, not rejected.
/// </summary>
public static class TehranTime
{
    public static readonly TimeSpan Offset = new(3, 30, 0);

    public const string ZoneId = "Asia/Tehran";

    public static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0)
    {
        return new DateTimeOffset(year, month, day, hour, minute, second, Offset);
    }

    public static DateTimeOffset At(LocalDate date, TimeOfDay time)
    {
        return new DateTimeOffset(date.Year, date.Month, date.Day, time.Hour, time.Minute, time.Second, Offset);
    }

    /// <summary>
    /// The same instant in Tehran. The offset on <paramref name="value"/> is not a time zone:
    /// the clock is rebuilt from the UTC instant at a fixed +03:30.
    /// </summary>
    public static DateTimeOffset Ensure(DateTimeOffset value)
    {
        var utc = DateTime.SpecifyKind(value.UtcDateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(utc.Add(Offset), Offset);
    }

    /// <summary>
    /// A UTC clock, or an unspecified clock that is already UTC. A <see cref="DateTimeKind.Local"/>
    /// value is rejected so the machine's offset is never applied.
    /// </summary>
    public static DateTimeOffset FromUtc(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
        {
            throw new BillingException(BillingErrors.InvalidTime, "FromUtc expects a UTC or unspecified DateTime, not a local one.");
        }

        var clock = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified).Add(Offset);
        return new DateTimeOffset(clock, Offset);
    }

    public static LocalDate Date(DateTimeOffset value)
    {
        var local = Ensure(value);
        return new LocalDate(local.Year, local.Month, local.Day);
    }

    public static TimeOfDay TimeOfDay(DateTimeOffset value)
    {
        var local = Ensure(value);
        return new TimeOfDay(local.Hour, local.Minute, local.Second);
    }

    /// <summary>
    /// The next strict occurrence of <paramref name="time"/> after <paramref name="cursor"/>.
    /// An instant that already sits on the mark rolls forward one day.
    /// </summary>
    public static DateTimeOffset NextWallTime(DateTimeOffset cursor, TimeOfDay time)
    {
        cursor = Ensure(cursor);
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
        return NextWallTime(cursor, GameClub.Billing.TimeOfDay.Midnight);
    }

    public static long WholeSeconds(DateTimeOffset start, DateTimeOffset end)
    {
        start = Ensure(start);
        end = Ensure(end);
        var ticks = DurationTicks(start, end);
        return ticks / TimeSpan.TicksPerSecond;
    }

    public static long DurationTicks(DateTimeOffset start, DateTimeOffset end)
    {
        start = Ensure(start);
        end = Ensure(end);
        var ticks = (end - start).Ticks;
        if (ticks < 0)
        {
            throw new BillingException(BillingErrors.InvalidTime, "End is before start.");
        }

        return ticks;
    }

    public static DateTimeOffset AddSeconds(DateTimeOffset value, long seconds)
    {
        value = Ensure(value);
        try
        {
            return value.AddTicks(checked(seconds * TimeSpan.TicksPerSecond));
        }
        catch (OverflowException)
        {
            throw new BillingException(BillingErrors.InvalidTime, "Adding those seconds overflowed the clock.");
        }
    }
}
