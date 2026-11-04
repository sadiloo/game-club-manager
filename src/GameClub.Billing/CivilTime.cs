namespace GameClub.Billing;

/// <summary>
/// A Gregorian calendar date with no time and no offset. This is the public stand-in for
/// <c>DateOnly</c>, which netstandard2.0 (and therefore .NET Framework 4.8) does not have.
/// </summary>
public readonly record struct LocalDate : IComparable<LocalDate>
{
    public LocalDate(int year, int month, int day)
    {
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            throw new BillingException(BillingErrors.InvalidDate, $"Gregorian date {year:0000}-{month:00}-{day:00} does not exist.");
        }

        Year = year;
        Month = month;
        Day = day;
    }

    public int Year { get; }

    public int Month { get; }

    public int Day { get; }

    public static LocalDate MaxValue { get; } = new(9999, 12, 31);

    public DayOfWeek DayOfWeek
    {
        get
        {
            if (Year is < 1 or > 9999 || Month is < 1 or > 12 || Day < 1)
            {
                throw new BillingException(BillingErrors.InvalidDate, "The date is empty.");
            }

            try
            {
                return new DateTime(Year, Month, Day).DayOfWeek;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new BillingException(BillingErrors.InvalidDate, $"Gregorian date {Year:0000}-{Month:00}-{Day:00} does not exist.");
            }
        }
    }

    public LocalDate AddDays(int days)
    {
        var shifted = new DateTime(Year, Month, Day).AddDays(days);
        return new LocalDate(shifted.Year, shifted.Month, shifted.Day);
    }

    public int CompareTo(LocalDate other)
    {
        var year = Year.CompareTo(other.Year);
        if (year != 0)
        {
            return year;
        }

        var month = Month.CompareTo(other.Month);
        if (month != 0)
        {
            return month;
        }

        return Day.CompareTo(other.Day);
    }

    public static bool operator <(LocalDate left, LocalDate right) => left.CompareTo(right) < 0;

    public static bool operator >(LocalDate left, LocalDate right) => left.CompareTo(right) > 0;

    public static bool operator <=(LocalDate left, LocalDate right) => left.CompareTo(right) <= 0;

    public static bool operator >=(LocalDate left, LocalDate right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Year:0000}-{Month:00}-{Day:00}";
}

/// <summary>
/// A time of day with no date. This is the public stand-in for <c>TimeOnly</c>.
/// </summary>
public readonly record struct TimeOfDay : IComparable<TimeOfDay>
{
    public TimeOfDay(int hour, int minute, int second = 0)
    {
        if (hour is < 0 or > 23 || minute is < 0 or > 59 || second is < 0 or > 59)
        {
            throw new BillingException(BillingErrors.InvalidTime, $"Clock time {hour}:{minute:00}:{second:00} is not a time of day.");
        }

        Hour = hour;
        Minute = minute;
        Second = second;
    }

    public int Hour { get; }

    public int Minute { get; }

    public int Second { get; }

    public static TimeOfDay Midnight { get; } = new(0, 0, 0);

    public int SecondOfDay => (Hour * 3600) + (Minute * 60) + Second;

    public int CompareTo(TimeOfDay other) => SecondOfDay.CompareTo(other.SecondOfDay);

    public static bool operator <(TimeOfDay left, TimeOfDay right) => left.CompareTo(right) < 0;

    public static bool operator >(TimeOfDay left, TimeOfDay right) => left.CompareTo(right) > 0;

    public static bool operator <=(TimeOfDay left, TimeOfDay right) => left.CompareTo(right) <= 0;

    public static bool operator >=(TimeOfDay left, TimeOfDay right) => left.CompareTo(right) >= 0;

    public override string ToString() => $"{Hour:00}:{Minute:00}:{Second:00}";
}
