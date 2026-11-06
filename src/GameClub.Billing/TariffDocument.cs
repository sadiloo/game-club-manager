namespace GameClub.Billing;

/// <summary>
/// The rate card and special-day list a server sends to a station.
/// The kiosk prices with this document and does not keep a private hourly rate.
/// </summary>
public sealed class TariffDocument
{
    public List<BandDocument> Bands { get; set; } = [];

    public List<RateDocument> BaseRates { get; set; } = [];

    public List<RateDocument> BandRates { get; set; } = [];

    public List<RateDocument> WeekdayRates { get; set; } = [];

    public List<RateDocument> SpecialRates { get; set; } = [];

    public RateCard ToCard()
    {
        var builder = new RateCardBuilder();
        foreach (var band in Bands)
        {
            builder.AddBand(new TimeBand(band.Id, band.GroupId, band.Name, ParseClock(band.Start), ParseClock(band.End)));
        }

        foreach (var rate in BaseRates)
        {
            builder.AddBase(rate.GroupId, (PlayMode)rate.Mode, rate.PlayerCount, rate.HourlyToman);
        }

        foreach (var rate in BandRates)
        {
            builder.AddBandRate(rate.BandId ?? "", rate.GroupId, (PlayMode)rate.Mode, rate.PlayerCount, rate.HourlyToman);
        }

        foreach (var rate in WeekdayRates)
        {
            builder.AddWeekday((DayOfWeek)(rate.Weekday ?? 0), rate.GroupId, (PlayMode)rate.Mode, rate.PlayerCount, rate.HourlyToman);
        }

        foreach (var rate in SpecialRates)
        {
            builder.AddSpecialRate(rate.TableId ?? "", rate.GroupId, (PlayMode)rate.Mode, rate.PlayerCount, rate.HourlyToman);
        }

        return builder.Build();
    }

    private static TimeOfDay ParseClock(string text)
    {
        var parts = (text ?? "").Split(':');
        var hour = parts.Length > 0 ? int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var minute = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        var second = parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0;
        return new TimeOfDay(hour, minute, second);
    }
}

public sealed class BandDocument
{
    public string Id { get; set; } = "";

    public string GroupId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Start { get; set; } = "";

    public string End { get; set; } = "";
}

public sealed class RateDocument
{
    public string GroupId { get; set; } = "";

    public int Mode { get; set; }

    public int? PlayerCount { get; set; }

    public long HourlyToman { get; set; }

    public string? BandId { get; set; }

    public int? Weekday { get; set; }

    public string? TableId { get; set; }
}

public sealed class CalendarDocument
{
    public List<DayDocument> Days { get; set; } = [];

    public SpecialDayCalendar ToCalendar()
    {
        var days = new List<SpecialDay>();
        foreach (var day in Days)
        {
            days.Add(day.Year is int year
                ? SpecialDay.Once(new JalaliDate(year, day.Month, day.Day), day.Name, day.RateTableId)
                : SpecialDay.EveryYear(day.Month, day.Day, day.Name, day.RateTableId));
        }

        return new SpecialDayCalendar(days);
    }

    public static CalendarDocument From(SpecialDayCalendar calendar)
    {
        var document = new CalendarDocument();
        foreach (var day in calendar.Days)
        {
            if (day.Date is JalaliDate once)
            {
                document.Days.Add(new DayDocument
                {
                    Name = day.Name,
                    RateTableId = day.RateTableId,
                    Year = once.Year,
                    Month = once.Month,
                    Day = once.Day,
                });
            }
            else
            {
                document.Days.Add(new DayDocument
                {
                    Name = day.Name,
                    RateTableId = day.RateTableId,
                    Month = day.Month ?? 0,
                    Day = day.Day ?? 0,
                });
            }
        }

        return document;
    }
}

public sealed class DayDocument
{
    public string Name { get; set; } = "";

    public string RateTableId { get; set; } = "";

    public int? Year { get; set; }

    public int Month { get; set; }

    public int Day { get; set; }
}
