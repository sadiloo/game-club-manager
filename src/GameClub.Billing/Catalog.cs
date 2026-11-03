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
    Holiday = 3,
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

            if (!_groups.TryAdd(group.Id, group))
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

            if (!_stations.TryAdd(station.Id, station))
            {
                throw new BillingException("invalid_tariff", $"Duplicate station '{station.Id}'.");
            }
        }
    }

    public StationGroup GroupOf(string stationId)
    {
        if (!_stations.TryGetValue(stationId, out var station))
        {
            throw new BillingException("invalid_session", $"Unknown station '{stationId}'.");
        }

        return _groups[station.GroupId];
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
        return new PlaySegment(id, start, end, stationId, group.Id, mode, playerCount);
    }
}

/// <summary>
/// Half-open local-time interval <c>[Start, End)</c>. When <c>End &lt;= Start</c> the band wraps midnight.
/// </summary>
public sealed record TimeBand(string Id, string Name, TimeOnly Start, TimeOnly End)
{
    public bool WrapsMidnight => SecondOfDay(End) <= SecondOfDay(Start);

    public bool Contains(TimeOnly time)
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

    internal static int SecondOfDay(TimeOnly time) => (time.Hour * 3600) + (time.Minute * 60) + time.Second;
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

/// <summary>Holiday dates in the Tehran calendar. One holiday rate table on the <see cref="RateCard"/> applies to every date.</summary>
public sealed class HolidayCalendar
{
    private readonly HashSet<DateOnly> _dates;

    public HolidayCalendar(IEnumerable<DateOnly>? dates = null)
    {
        _dates = dates is null ? [] : [..dates];
    }

    public static HolidayCalendar Empty { get; } = new();

    public bool IsHoliday(DateOnly date) => _dates.Contains(date);

    public IReadOnlyCollection<DateOnly> Dates => _dates;
}

public sealed class RateCard
{
    private readonly Dictionary<TariffKey, long> _base;
    private readonly Dictionary<(string BandId, TariffKey Key), long> _bands;
    private readonly Dictionary<(DayOfWeek Day, TariffKey Key), long> _weekdays;
    private readonly Dictionary<TariffKey, long> _holidays;

    internal RateCard(
        IReadOnlyList<TimeBand> bands,
        Dictionary<TariffKey, long> baseRates,
        Dictionary<(string BandId, TariffKey Key), long> bandRates,
        Dictionary<(DayOfWeek Day, TariffKey Key), long> weekdayRates,
        Dictionary<TariffKey, long> holidayRates)
    {
        Bands = bands;
        _base = baseRates;
        _bands = bandRates;
        _weekdays = weekdayRates;
        _holidays = holidayRates;
    }

    public IReadOnlyList<TimeBand> Bands { get; }

    public bool TryBase(TariffKey key, out long hourly) => _base.TryGetValue(key, out hourly);

    public bool TryBand(string bandId, TariffKey key, out long hourly) => _bands.TryGetValue((bandId, key), out hourly);

    public bool TryWeekday(DayOfWeek day, TariffKey key, out long hourly) => _weekdays.TryGetValue((day, key), out hourly);

    public bool TryHoliday(TariffKey key, out long hourly) => _holidays.TryGetValue(key, out hourly);

    public TimeBand? BandAt(TimeOnly time)
    {
        TimeBand? found = null;
        foreach (var band in Bands)
        {
            if (!band.Contains(time))
            {
                continue;
            }

            if (found is not null)
            {
                throw new BillingException("invalid_tariff", $"Bands '{found.Id}' and '{band.Id}' both contain {time}.");
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
    private readonly Dictionary<TariffKey, long> _holidays = [];

    public RateCardBuilder AddBand(TimeBand band)
    {
        if (string.IsNullOrWhiteSpace(band.Id))
        {
            throw new BillingException("invalid_tariff", "Time band id is required.");
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
        if (!_base.TryAdd(key, hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate base rate for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddBandRate(string bandId, string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!_bandRates.TryAdd((bandId, key), hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate band rate '{bandId}' for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddWeekday(DayOfWeek day, string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!_weekdays.TryAdd((day, key), hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate {day} rate for {Describe(key)}.");
        }

        return this;
    }

    public RateCardBuilder AddHoliday(string groupId, PlayMode mode, int? playerCount, long hourlyRateToman)
    {
        var key = TariffKey.Create(groupId, mode, playerCount);
        RequireRate(hourlyRateToman);
        if (!_holidays.TryAdd(key, hourlyRateToman))
        {
            throw new BillingException("invalid_tariff", $"Duplicate holiday rate for {Describe(key)}.");
        }

        return this;
    }

    public RateCard Build()
    {
        EnsureNoOverlap();
        var known = _bands.Select(band => band.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (bandId, _) in _bandRates.Keys)
        {
            if (!known.Contains(bandId))
            {
                throw new BillingException("invalid_tariff", $"Band rate references unknown band '{bandId}'.");
            }
        }

        return new RateCard(
            _bands.ToArray(),
            new Dictionary<TariffKey, long>(_base),
            new Dictionary<(string BandId, TariffKey Key), long>(_bandRates),
            new Dictionary<(DayOfWeek Day, TariffKey Key), long>(_weekdays),
            new Dictionary<TariffKey, long>(_holidays));
    }

    private void EnsureNoOverlap()
    {
        var ranges = new List<(int Start, int End, string Id)>();
        foreach (var band in _bands)
        {
            var start = TimeBand.SecondOfDay(band.Start);
            var end = TimeBand.SecondOfDay(band.End);
            if (start < end)
            {
                ranges.Add((start, end, band.Id));
            }
            else
            {
                ranges.Add((start, 86_400, band.Id));
                ranges.Add((0, end, band.Id));
            }
        }

        for (var i = 0; i < ranges.Count; i++)
        {
            for (var j = i + 1; j < ranges.Count; j++)
            {
                var a = ranges[i];
                var b = ranges[j];
                if (a.Id == b.Id)
                {
                    continue;
                }

                if (a.Start < b.End && b.Start < a.End)
                {
                    throw new BillingException("invalid_tariff", $"Time bands '{a.Id}' and '{b.Id}' overlap.");
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
    /// Precedence is holiday, then weekday, then the hour band, then the base tariff.
    /// A missing rate at a higher layer falls through, so a holiday does not need a
    /// copy of every mode. Online and VR ignore player count.
    /// </summary>
    public static RateQuote Resolve(
        RateCard card,
        HolidayCalendar holidays,
        DateTimeOffset instant,
        string groupId,
        PlayMode mode,
        int playerCount)
    {
        TehranTime.Ensure(instant);
        var key = TariffKey.ForSegment(groupId, mode, playerCount);
        var date = TehranTime.Date(instant);
        var time = TehranTime.TimeOfDay(instant);

        if (holidays.IsHoliday(date) && card.TryHoliday(key, out var holidayRate))
        {
            return new RateQuote(holidayRate, RateSource.Holiday, null);
        }

        if (card.TryWeekday(date.DayOfWeek, key, out var weekdayRate))
        {
            return new RateQuote(weekdayRate, RateSource.Weekday, null);
        }

        var band = card.BandAt(time);
        if (band is not null && card.TryBand(band.Id, key, out var bandRate))
        {
            return new RateQuote(bandRate, RateSource.TimeBand, band.Id);
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
    public static DateTimeOffset Next(DateTimeOffset cursor, RateCard card)
    {
        var next = TehranTime.NextMidnight(cursor);
        foreach (var band in card.Bands)
        {
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
