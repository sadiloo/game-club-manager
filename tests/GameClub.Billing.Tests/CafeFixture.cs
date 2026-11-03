using GameClub.Billing;

namespace GameClub.Billing.Tests;

internal static class T
{
    public static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0)
        => TehranTime.At(year, month, day, hour, minute, second);
}

/// <summary>
/// PS5 prices are the Game Timer export. VIP and معمولی rates were blank in that
/// export; the figures below are explicit hypotheticals so account and transfer
/// tests have a second group.
/// </summary>
internal static class CafeFixture
{
    public const string Ps5 = GameTimerReference.Ps5GroupId;
    public const string Vip = "vip";
    public const string Normal = "normal";

    /// <summary>Hypothetical. Not in the Game Timer export.</summary>
    public const long VipOnePlayerHourly = 220_000;

    /// <summary>Hypothetical ordinary PC rate, equal to the published PS5 1-player rate so estimates divide cleanly.</summary>
    public const long NormalOnePlayerHourly = GameTimerReference.Ps5OnePlayerHourly;

    public static RateCard Card()
    {
        return new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBase(Ps5, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerHourly)
            .AddBase(Ps5, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerHourly)
            .AddBase(Ps5, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerHourly)
            .AddBase(Ps5, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerHourly)
            .AddBase(Ps5, PlayMode.Online, null, GameTimerReference.Ps5OnlineHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, Ps5, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, Ps5, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, Ps5, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, Ps5, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, Ps5, PlayMode.Online, null, GameTimerReference.Ps5OnlineSpecialHourly)
            .AddBase(Vip, PlayMode.Offline, 1, VipOnePlayerHourly)
            .AddBase(Vip, PlayMode.Offline, 2, 250_000)
            .AddBase(Vip, PlayMode.Offline, 3, 300_000)
            .AddBase(Vip, PlayMode.Offline, 4, 360_000)
            .AddBase(Normal, PlayMode.Offline, 1, NormalOnePlayerHourly)
            .AddBase(Normal, PlayMode.Offline, 2, 130_000)
            .AddBase(Normal, PlayMode.Offline, 3, 150_000)
            .AddBase(Normal, PlayMode.Offline, 4, 180_000)
            .Build();
    }

    public static StationDirectory Directory()
    {
        return new StationDirectory(
            [
                new StationGroup(Ps5, "PS5", StationKind.Console),
                new StationGroup(Vip, "VIP", StationKind.Console),
                new StationGroup(Normal, "معمولی", StationKind.Pc),
            ],
            [
                new Station("ps5-1", "ps5 (1)", Ps5),
                new Station("ps5-3", "ps5 (3)", Ps5),
                new Station("ps5-6", "ps5 (6)", Ps5),
                new Station("vip-1", "VIP 1", Vip),
                new Station("pc-1", "PC 1", Normal),
            ]);
    }

    public static Session Session(string id, params PlaySegment[] segments)
        => new(id, segments);

    public static PlaySegment Play(
        string id,
        DateTimeOffset start,
        DateTimeOffset end,
        string stationId,
        string groupId,
        int players,
        PlayMode mode = PlayMode.Offline)
        => new(id, start, end, stationId, groupId, mode, players);

    public static SessionCharge Price(Session session, BillingPolicy? policy = null, HolidayCalendar? holidays = null)
        => SessionPricer.Price(session, Card(), holidays, policy);
}
