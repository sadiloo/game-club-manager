using System.Globalization;

namespace GameClub.Billing;

/// <summary>
/// Solar Hijri (Jalali) month numbers. Farvardin is 1 and Esfand is 12.
/// Mehr, the seventh month, has 30 days.
/// </summary>
public static class JalaliMonth
{
    public const int Farvardin = 1;
    public const int Ordibehesht = 2;
    public const int Khordad = 3;
    public const int Tir = 4;
    public const int Mordad = 5;
    public const int Shahrivar = 6;
    public const int Mehr = 7;
    public const int Aban = 8;
    public const int Azar = 9;
    public const int Dey = 10;
    public const int Bahman = 11;
    public const int Esfand = 12;
}

/// <summary>
/// Solar Hijri dates for an Iranian cafe. The stored birthday and special-day
/// form is Jalali year/month/day. A Gregorian date is accepted only as a Tehran
/// civil date that is converted with <see cref="FromGregorian"/>; the engine then
/// matches on the Jalali calendar. Conversion uses <see cref="PersianCalendar"/>.
/// </summary>
public readonly record struct JalaliDate
{
    public int Year { get; }
    public int Month { get; }
    public int Day { get; }

    public JalaliDate(int year, int month, int day)
    {
        JalaliCalendar.EnsureExists(year, month, day);
        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>True for <c>default(JalaliDate)</c>, which bypasses the constructor.</summary>
    public bool IsEmpty => Year == 0 && Month == 0 && Day == 0;

    public static JalaliDate FromGregorian(LocalDate tehranDate)
    {
        var parts = JalaliCalendar.FromGregorian(tehranDate);
        return new JalaliDate(parts.Year, parts.Month, parts.Day);
    }

    public LocalDate ToGregorian()
    {
        if (IsEmpty)
        {
            throw new BillingException(BillingErrors.InvalidDate, "Jalali date is empty.");
        }

        return JalaliCalendar.ToGregorian(Year, Month, Day);
    }

    /// <summary>
    /// This month and day in <paramref name="jalaliYear"/>. Esfand 30, which exists
    /// only in a leap year, is observed on Esfand 29 when that year is not a leap year.
    /// </summary>
    public JalaliDate ObservedIn(int jalaliYear)
    {
        if (IsEmpty)
        {
            throw new BillingException(BillingErrors.InvalidDate, "Jalali date is empty.");
        }

        if (Month == JalaliMonth.Esfand && Day == 30 && !JalaliCalendar.IsLeapYear(jalaliYear))
        {
            return new JalaliDate(jalaliYear, JalaliMonth.Esfand, 29);
        }

        return new JalaliDate(jalaliYear, Month, Day);
    }

    public override string ToString() => $"{Year:0000}/{Month:00}/{Day:00}";
}

public static class JalaliCalendar
{
    private static readonly PersianCalendar Calendar = new();

    public static bool IsLeapYear(int jalaliYear) => Calendar.IsLeapYear(jalaliYear);

    public static void EnsureExists(int year, int month, int day)
    {
        try
        {
            _ = Calendar.ToDateTime(year, month, day, 0, 0, 0, 0);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new BillingException(BillingErrors.InvalidDate, $"Jalali date {year:0000}/{month:00}/{day:00} does not exist.");
        }
    }

    public static (int Year, int Month, int Day) FromGregorian(LocalDate tehranDate)
    {
        var gregorian = new DateTime(tehranDate.Year, tehranDate.Month, tehranDate.Day);
        return (Calendar.GetYear(gregorian), Calendar.GetMonth(gregorian), Calendar.GetDayOfMonth(gregorian));
    }

    public static LocalDate ToGregorian(int year, int month, int day)
    {
        try
        {
            var date = Calendar.ToDateTime(year, month, day, 0, 0, 0, 0);
            return new LocalDate(date.Year, date.Month, date.Day);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new BillingException(BillingErrors.InvalidDate, $"Jalali date {year:0000}/{month:00}/{day:00} does not exist.");
        }
    }

    /// <summary>
    /// Month and day of a yearly anniversary in <paramref name="jalaliYear"/>.
    /// Esfand 30 is observed on Esfand 29 when the year is not a leap year.
    /// </summary>
    public static JalaliDate ObservedAnniversary(int month, int day, int jalaliYear)
    {
        if (month == JalaliMonth.Esfand && day == 30 && !IsLeapYear(jalaliYear))
        {
            return new JalaliDate(jalaliYear, JalaliMonth.Esfand, 29);
        }

        return new JalaliDate(jalaliYear, month, day);
    }
}
