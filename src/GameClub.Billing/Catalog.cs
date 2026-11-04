namespace GameClub.Billing;

public enum StationKind
{
    Pc = 0,
    Console = 1,
    TimedTable = 2,
}

public enum PlayMode
{
    /// <summary>Priced by player / controller count (1–4).</summary>
    Offline = 0,

    /// <summary>Single rate. Player count is recorded but does not select the tariff.</summary>
    Online = 1,

    /// <summary>Single rate, same lookup rule as <see cref="Online"/>.</summary>
    Vr = 2,
}

public enum RateSource
{
    BaseTariff = 0,
    TimeBand = 1,
    Weekday = 2,
    SpecialDay = 3,
}

/// <summary>A user-defined station group: معمولی, CIP, VIP, VVIP, a console model, a table game, and so on.</summary>
public sealed record StationGroup(string Id, string Name, StationKind Kind);

public sealed record Station(string Id, string Name, string GroupId);

/// <summary>Each station belongs to exactly one group. Transferring stations is how a session changes group.</summary>
public sealed class StationDirectory
{
    private readonly Dictionary<string, Station> _stations;
    private readonly Dictionary<string, StationGroup> _groups;

    public StationDirectory(IEnumerable<StationGroup> groups, IEnumerable<Station> stations)
    {
        _groups = new Dictionary<string, StationGroup>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id))
            {
                throw new BillingException("invalid_tariff", "Station group id is required.");
            }

            if (!Bcl.TryAdd(_groups, group.Id, group))
            {
                throw new BillingException("invalid_tariff", $"Duplicate station group '{group.Id}'.");
            }
        }

        _stations = new Dictionary<string, Station>(StringComparer.Ordinal);
        foreach (var station in stations)
        {
            if (string.IsNullOrWhiteSpace(station.Id))
            {
                throw new BillingException("invalid_tariff", "Station id is required.");
            }

            if (!_groups.ContainsKey(station.GroupId))
            {
                throw new BillingException("invalid_tariff", $"Station '{station.Id}' references unknown group '{station.GroupId}'.");
            }

            if (!Bcl.TryAdd(_stations, station.Id, station))
            {
                throw new BillingException("invalid_tariff", $"Duplicate station '{station.Id}'.");
            }
        }
    }

    public bool TryGetGroup(string stationId, out StationGroup group)
    {
        if (_stations.TryGetValue(stationId, out var station) && _groups.TryGetValue(station.GroupId, out group!))
        {
            return true;
        }

        group = null!;
        return false;
    }

    public StationGroup GroupOf(string stationId)
    {
        if (!TryGetGroup(stationId, out var group))
        {
            throw new BillingException(BillingErrors.InvalidSession, $"Unknown station '{stationId}'.");
        }

        return group;
    }

    public PlaySegment Segment(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        string stationId,
        PlayMode mode,
        int playerCount)
    {
        var group = GroupOf(stationId);
        return new PlaySegment(id, start, end, stationId, group.Id, mode, playerCount, group.Kind);
    }
}

/// <summary>
/// Half-open local-time interval <c>[Start, End)</c> belonging to one station group.
/// When <c>End &lt;= Start</c> the band wraps midnight. Another group's bands do not
/// split or reprice this group's sessions.
/// </summary>
public sealed record TimeBand(string Id, string GroupId, string Name, TimeOfDay Start, TimeOfDay End)
{
    public bool WrapsMidnight => SecondOfDay(End) <= SecondOfDay(Start);

    public bool Contains(TimeOfDay time)
    {
        var t = SecondOfDay(time);
        var start = SecondOfDay(Start);
        var end = SecondOfDay(End);
        if (start == end)
        {
            return false;
        }

        if (start < end)
        {
            return t >= start && t < end;
        }

        return t >= start || t < end;
    }

    internal static int SecondOfDay(TimeOfDay time) => time.SecondOfDay;
}

public readonly record struct TariffKey(string GroupId, PlayMode Mode, int? PlayerCount)
{
    public static TariffKey Create(string groupId, PlayMode mode, int? playerCount)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            throw new BillingException("invalid_tariff", "Group id is required.");
        }

        if (mode == PlayMode.Offline)
        {
            if (playerCount is < 1 or > 4)
            {
                throw new BillingException("invalid_tariff", $"Offline rates require 1–4 players, got {playerCount?.ToString() ?? "none"}.");
            }

            return new TariffKey(groupId, mode, playerCount);
        }

        if (playerCount is not null)
        {
            throw new BillingException("invalid_tariff", "Online and VR rates are not priced per player count.");
        }

        return new TariffKey(groupId, mode, null);
    }

    public static TariffKey ForSegment(string groupId, PlayMode mode, int playerCount)
    {
        if (playerCount is < 0 or > 4)
        {
            throw new BillingException("invalid_session", $"Player count must be 0–4, got {playerCount}.");
        }

        if (mode == PlayMode.Offline && playerCount is < 1 or > 4)
        {
            throw new BillingException("invalid_session", $"Offline play requires 1–4 players, got {playerCount}.");
        }

        return Create(groupId, mode, mode == PlayMode.Offline ? playerCount : null);
    }
}

public sealed record TariffRate(string GroupId, PlayMode Mode, int? PlayerCount, long HourlyRateToman);

public sealed record BandTariff(string BandId, string GroupId, PlayMode Mode, int? PlayerCount, long HourlyRateToman);

public sealed record WeekdayTariff(DayOfWeek Day, string GroupId, PlayMode Mode, int? PlayerCount, long HourlyRateToman);

public readonly record struct RateQuote(long HourlyRateToman, RateSource Source, string? BandId);

/// <summary>
/// A cafe-defined holiday or other special day on the Solar Hijri calendar.
/// There is no built-in official list. A day is either one Jalali date or a yearly
/// month/day such as 1 Farvardin. Several rules may share one rate table.
/// Rates on that table are per group. A one-off date wins over a yearly rule on the same day.
/// A yearly Esfand 30 is observed on Esfand 29 when the Jalali year is not a leap year.
/// </summary>
public sealed record SpecialDay
{
    private SpecialDay(string name, string rateTableId, JalaliDate? date, int? month, int? day)
    {
        Name = name;
        RateTableId = rateTableId;
        Date = date;
        Month = month;
        Day = day;
    }

    public string Name { get; }

    public string RateTableId { get; }

    /// <summary>Set for a one-off Jalali date. Null when the rule repeats every year.</summary>
    public JalaliDate? Date { get; }

    /// <summary>Jalali month of a yearly rule. Null for a one-off date.</summary>
    public int? Month { get; }

    /// <summary>Jalali day of a yearly rule. Null for a one-off date.</summary>
    public int? Day { get; }

    public bool IsRecurring => Date is null;

    public static SpecialDay Once(JalaliDate date, string name, string rateTableId)
    {
        RequireLabel(name, rateTableId);
        if (date.IsEmpty)
        {
            throw new BillingException(BillingErrors.InvalidDate, "A one-off special day needs a Jalali date.");
        }

        return new SpecialDay(name, rateTableId, date, null, null);
    }

    public static SpecialDay EveryYear(int month, int day, string name, string rateTableId)
    {
        RequireLabel(name, rateTableId);
        if (month is < JalaliMonth.Farvardin or > JalaliMonth.Esfand)
        {
            throw new BillingException(BillingErrors.InvalidDate, $"Jalali month {month} is outside 1–12.");
        }

        // Months 1–6 have 31 days, 7–11 have 30, and Esfand allows day 30 as an anniversary
        // even though that day exists only in a leap year. No sample year is hard-coded.
        var maxDay = month <= 6 ? 31 : 30;
        if (day < 1 || day > maxDay)
        {
            throw new BillingException(BillingErrors.InvalidDate, $"Jalali day {day} is not valid in month {month}.");
        }

        return new SpecialDay(name, rateTableId, null, month, day);
    }

    public bool Matches(JalaliDate on)
    {
        if (Date is JalaliDate once)
        {
            return once == on;
        }

        var observed = JalaliCalendar.ObservedAnniversary(Month!.Value, Day!.Value, on.Year);
        return on.Month == observed.Month && on.Day == observed.Day;
    }

    private static void RequireLabel(string name, string rateTableId)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rateTableId))
        {
            throw new BillingException("invalid_tariff", "A special day needs a name and a rate table id.");
        }
    }
}

/// <summary>User-maintained Jalali special days. The cafe adds and removes them; nothing is hard-coded.</summary>
public sealed class SpecialDayCalendar
{
    private readonly SpecialDay[] _days;

    public SpecialDayCalendar(IEnumerable<SpecialDay>? days = null)
    {
        var list = new List<SpecialDay>();
        var oneOffs = new HashSet<JalaliDate>();
        var yearly = new HashSet<(int Month, int Day)>();
        foreach (var day in days ?? [])
        {
            if (day.Date is JalaliDate once)
            {
                if (!oneOffs.Add(once))
                {
                    throw new BillingException("invalid_tariff", $"Two one-off special days are set on {once}.");
                }
            }
            else if (!yearly.Add((day.Month!.Value, day.Day!.Value)))
            {
                throw new BillingException("invalid_tariff", $"Two yearly special days are set on month {day.Month} day {day.Day}.");
            }

            list.Add(day);
        }

        _days = list.ToArray();
    }

    public static SpecialDayCalendar Empty { get; } = new();

    /// <summary>
    /// The special day in force on this Tehran civil date, or null.
    /// A one-off Jalali date wins when a yearly rule falls on the same day.
    /// </summary>
    public SpecialDay? On(LocalDate tehranDate)
    {
        var on = JalaliDate.FromGregorian(tehranDate);
        SpecialDay? oneOff = null;
        SpecialDay? recurring = null;
        foreach (var day in _days)
        {
            if (!day.Matches(on))
            {
                continue;
            }

            if (day.IsRecurring)
            {
                recurring = day;
            }
            else
            {
                oneOff = day;
            }
        }

        return oneOff ?? recurring;
    }

    public IReadOnlyCollection<SpecialDay> Days => _days;
}

public sealed class RateCard
{
    private readonly Dictionary<TariffKey, long> _base;
    private readonly Dictionary<(string BandId, TariffKey Key), long> _bands;
    private readonly Dictionary<(DayOfWeek Day, TariffKey Key), long> _weekdays;
    private readonly Dictionary<(string TableId, TariffKey Key), long> _special;

    internal RateCard(
        IReadOnlyList<TimeBand> bands,
        Dictionary<TariffKey, long> baseRates,
        Dictionary<(string BandId, TariffKey Key), long> bandRates,
        Dictionary<(DayOfWeek Day, TariffKey Key), long> weekdayRates,
        Dictionary<(string TableId, TariffKey Key), long> specialRates)
    {
        Bands = new System.Collections.ObjectModel.ReadOnlyCollection<TimeBand>(bands.ToArray());
        _base = baseRates;
        _bands = bandRates;
        _weekdays = weekdayRates;
        _special = specialRates;
    }

    public IReadOnlyList<TimeBand> Bands { get; }

    public bool TryBase(TariffKey key, out long hourly) => _base.TryGetValue(key, out hourly);

    public bool TryBand(string bandId, TariffKey key, out long hourly) => _bands.TryGetValue((bandId, key), out hourly);

    public bool TryWeekday(DayOfWeek day, TariffKey key, out long hourly) => _weekdays.TryGetValue((day, key), out hourly);

    public bool TrySpecial(string rateTableId, TariffKey key, out long hourly) => _special.TryGetValue((rateTableId, key), out hourly);

    public TimeBand? BandAt(string groupId, TimeOfDay time)
    {
        TimeBand? found = null;
        foreach (var band in Bands)
        {
            if (!string.Equals(band.GroupId, groupId, StringComparison.Ordinal) || !band.Contains(time))
            {
                continue;
            }

            if (found is not null)
            {
                throw new BillingException("invalid_tariff", $"Bands '{found.Id}' and '{band.Id}' of group '{groupId}' both contain {time}.");
            }

            found = band;
        }

        return found;
    }
}

public sealed class RateCardBuilder
{
    private readonly List<TimeBand> _bands = [];
    private readonly Dictionary<TariffKey, long> _base = [];
    private readonly Dictionary<(string BandId, TariffKey Key), long> _bandRates = [];
    private readonly Dictionary<(DayOfWeek Day, TariffKey Key), long> _weekdays = [];
    private readonly Dictionary<(string TableId, TariffKey Key), long> _special = [];

    public RateCardBuilder AddBand(TimeBand band)
    {
        if (string.IsNullOrWhiteSpace(band.Id) || string.IsNullOrWhiteSpace(band.GroupId))
        {
            throw new BillingException("invalid_tariff", "Time band id and group id are required.");
        }

        if (_bands.Any(existing => string.Equals(existing.Id, band.Id, StringComparison.Ordinal)))
        {
            throw new BillingException("invalid_tariff", $"Duplicate time band '{band.Id}'.");
        }

        if (TimeBand.SecondOfDay(band.Start) == TimeBand.SecondOfDay(band.End))
        {
            throw new BillingException("invalid_tariff", $"Time band '{band.Id}' is empty.");
        }

        _bands.Add(band);
        return this;
    }

    public RateCardBuilder AddBase(string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!Bcl.TryAdd(_base, key, hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate base rate for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddBandRate(string bandId, string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!Bcl.TryAdd(_bandRates, (bandId, key), hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate band rate '{bandId}' for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddWeekday(DayOfWeek day, string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!Bcl.TryAdd(_weekdays, (day, key), hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate {day} rate for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddSpecialRate(string rateTableId, string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        if (string.IsNullOrWhiteSpace(rateTableId))
        {
            throw new BillingException("invalid_tariff", "Special-day rate table id is required.");
        }

        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!Bcl.TryAdd(_special, (rateTableId, key), hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate special rate '{rateTableId}' for {Describe(key)}.");
        }

        return this;
    }

    public RateCard Build()
    {
        EnsureNoOverlap();
        var known = _bands.ToDictionary(band => band.Id, StringComparer.Ordinal);
        foreach (var (bandId, key) in _bandRates.Keys)
        {
            if (!known.TryGetValue(bandId, out var band))
            {
                throw new BillingException("invalid_tariff", $"Band rate references unknown band '{bandId}'.");
            }

            if (!string.Equals(band.GroupId, key.GroupId, StringComparison.Ordinal))
            {
                throw new BillingException("invalid_tariff", $"Band '{bandId}' belongs to group '{band.GroupId}', not '{key.GroupId}'.");
            }
        }

        return new RateCard(
            _bands.ToArray(),
            new Dictionary<TariffKey, long>(_base),
            new Dictionary<(string BandId, TariffKey Key), long>(_bandRates),
            new Dictionary<(DayOfWeek Day, TariffKey Key), long>(_weekdays),
            new Dictionary<(string TableId, TariffKey Key), long>(_special));
    }

    private void EnsureNoOverlap()
    {
        var ranges = new List<(int Start, int End, string Id, string GroupId)>();
        foreach (var band in _bands)
        {
            var start = TimeBand.SecondOfDay(band.Start);
            var end = TimeBand.SecondOfDay(band.End);
            if (start < end)
            {
                ranges.Add((start, end, band.Id, band.GroupId));
            }
            else
            {
                ranges.Add((start, 86_400, band.Id, band.GroupId));
                ranges.Add((0, end, band.Id, band.GroupId));
            }
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                var a = ranges[i];
                var b = ranges[j];
                if (a.Id == b.Id || !string.Equals(a.GroupId, b.GroupId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (a.Start < b.End && b.Start < a.End)
                {
                    throw new BillingException("invalid_tariff", $"Time bands '{a.Id}' and '{b.Id}' of group '{a.GroupId}' overlap.");
                }
            }
        }
    }

    private static void RequireRate(long hourly)
    {
        if (hourly < 0)
        {
            throw new BillingException("invalid_money", "Hourly rate cannot be negative.");
        }
    }

    private static string Describe(TariffKey key)
    {
        return key.PlayerCount is int players
            ? $"{key.GroupId}/{key.Mode}/{players}p"
            : $"{key.GroupId}/{key.Mode}";
    }
}

public static class RateResolver
{
    /// <summary>
    /// Precedence is the cafe's special day for this group, then that group's own
    /// special-hours band, then the weekday rate, then the base tariff.
    /// A missing rate falls through. Bands of other groups are ignored.
    /// Online and VR ignore player count. Thursday 02:00 on PS5 is the band, not the weekday.
    /// </summary>
    public static RateQuote Resolve(
        RateCard card,
        SpecialDayCalendar specialDays,
        DateTimeOffset instant,
        string groupId,
        PlayMode mode,
        int playerCount)
    {
        instant = TehranTime.Ensure(instant);
        var key = TariffKey.ForSegment(groupId, mode, playerCount);
        var date = TehranTime.Date(instant);
        var time = TehranTime.TimeOfDay(instant);

        var special = specialDays.On(date);
        if (special is not null && card.TrySpecial(special.RateTableId, key, out var specialRate))
        {
            return new RateQuote(specialRate, RateSource.SpecialDay, special.RateTableId);
        }

        var band = card.BandAt(groupId, time);
        if (band is not null && card.TryBand(band.Id, key, out var bandRate))
        {
            return new RateQuote(bandRate, RateSource.TimeBand, band.Id);
        }

        if (card.TryWeekday(date.DayOfWeek, key, out var weekdayRate))
        {
            return new RateQuote(weekdayRate, RateSource.Weekday, null);
        }

        if (card.TryBase(key, out var baseRate))
        {
            return new RateQuote(baseRate, RateSource.BaseTariff, null);
        }

        throw new BillingException(
            "tariff_not_found",
            $"No tariff for group '{groupId}', mode {mode}, players {playerCount} at {instant:O}.");
    }
}

internal static class RateBoundaries
{
    public static DateTimeOffset Next(DateTimeOffset cursor, RateCard card, string groupId)
    {
        var next = TehranTime.NextMidnight(cursor);
        foreach (var band in card.Bands)
        {
            if (!string.Equals(band.GroupId, groupId, StringComparison.Ordinal))
            {
                continue;
            }

            var start = TehranTime.NextWallTime(cursor, band.Start);
            var end = TehranTime.NextWallTime(cursor, band.End);
            if (start < next)
            {
                next = start;
            }

            if (end < next)
            {
                next = end;
            }
        }

        if (next <= cursor)
        {
            throw new BillingException("invalid_time", "The next rate boundary did not advance.");
        }

        return next;
    }
}
