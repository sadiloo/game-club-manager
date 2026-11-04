using GameClub.Billing;

namespace GameClub.Billing.Tests;

internal static class T
{
    public static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0)
        => TehranTime.At(year, month, day, hour, minute, second);
}

/// <summary>
/// PS5 prices are the Game Timer export. The owner has no other live rates yet.
/// <see cref="ExampleVip"/> and <see cref="ExampleNormal"/> exist only so tests can
/// show a second group. They are not cafe prices.
/// </summary>
internal static class CafeFixture
{
    public const string Ps5 = GameTimerReference.Ps5GroupId;
    public const string ExampleVip = "example-vip";
    public const string ExampleNormal = "example-normal";

    /// <summary>Example rate for tests only. Not a Game Timer price and not a live VIP tariff.</summary>
    public const long ExampleVipOnePlayerHourly = 220_000;

    /// <summary>Example ordinary rate for tests only, set equal to the published PS5 1-player rate so estimates divide cleanly.</summary>
    public const long ExampleNormalOnePlayerHourly = GameTimerReference.Ps5OnePlayerHourly;

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
            .AddBase(ExampleVip, PlayMode.Offline, 1, ExampleVipOnePlayerHourly)
            .AddBase(ExampleVip, PlayMode.Offline, 2, 250_000)
            .AddBase(ExampleVip, PlayMode.Offline, 3, 300_000)
            .AddBase(ExampleVip, PlayMode.Offline, 4, 360_000)
            .AddBase(ExampleNormal, PlayMode.Offline, 1, ExampleNormalOnePlayerHourly)
            .AddBase(ExampleNormal, PlayMode.Offline, 2, 130_000)
            .AddBase(ExampleNormal, PlayMode.Offline, 3, 150_000)
            .AddBase(ExampleNormal, PlayMode.Offline, 4, 180_000)
            .Build();
    }

    public static StationDirectory Directory()
    {
        return new StationDirectory(
            [
                new StationGroup(Ps5, "PS5", StationKind.Console),
                new StationGroup(ExampleVip, "Example VIP", StationKind.Console),
                new StationGroup(ExampleNormal, "Example ordinary", StationKind.Pc),
            ],
            [
                new Station("ps5-1", "ps5 (1)", Ps5),
                new Station("ps5-3", "ps5 (3)", Ps5),
                new Station("ps5-6", "ps5 (6)", Ps5),
                new Station("example-vip-1", "Example VIP 1", ExampleVip),
                new Station("example-pc-1", "Example PC 1", ExampleNormal),
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
        => new(id, start, end, stationId, groupId, mode, players, StationKind.Console);

    public static SessionCharge Price(Session session, BillingPolicy? policy = null, SpecialDayCalendar? specialDays = null)
        => SessionPricer.Price(session, Card(), specialDays, policy);
}
