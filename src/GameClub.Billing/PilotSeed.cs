namespace GameClub.Billing;

/// <summary>
/// Pilot branch settings. These are cafe seed data, not live Game Timer tariffs and not
/// rules inside the pricing engine. A branch loads this record; the engine does not
/// special-case the numbers.
/// </summary>
public static class PilotSeed
{
    public const string PcNormalGroupId = "pc-normal";

    public const string PcVipGroupId = "pc-vip";

    public const string PcNormalBandId = "pc-normal-special-hours";

    public const string PcVipBandId = "pc-vip-special-hours";

    /// <summary>Pilot PC normal hourly rate, toman. Not a Game Timer price.</summary>
    public const long PcNormalHourly = 80_000;

    /// <summary>Pilot PC VIP hourly rate, toman. Not a Game Timer price.</summary>
    public const long PcVipHourly = 110_000;

    /// <summary>Pilot PC normal rate inside 00:00–08:30.</summary>
    public const long PcNormalSpecialHourly = 100_000;

    /// <summary>Pilot PC VIP rate inside 00:00–08:30.</summary>
    public const long PcVipSpecialHourly = 130_000;

    /// <summary>Same window as the console special-hours band. The band belongs to the PC group.</summary>
    public static TimeBand PcNormalSpecialHours { get; } = new(
        PcNormalBandId,
        PcNormalGroupId,
        "PC normal special hours 00:00–08:30",
        GameTimerReference.SpecialStart,
        GameTimerReference.SpecialEnd);

    public static TimeBand PcVipSpecialHours { get; } = new(
        PcVipBandId,
        PcVipGroupId,
        "PC VIP special hours 00:00–08:30",
        GameTimerReference.SpecialStart,
        GameTimerReference.SpecialEnd);

    /// <summary>
    /// Pilot rounding: on, step 5,000, up. The engine applies it only to the counter remainder.
    /// </summary>
    public static RoundingPolicy CounterRounding { get; } = RoundingPolicy.Create(5_000, RoundingDirection.Up, RoundingScope.WholeInvoice);

    /// <summary>PS5 live rates plus the pilot PC groups. PC lines follow bands and special days the same way consoles do.</summary>
    public static RateCard CreateCard()
    {
        return new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBase(GameTimerReference.Ps5GroupId, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerHourly)
            .AddBase(GameTimerReference.Ps5GroupId, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerHourly)
            .AddBase(GameTimerReference.Ps5GroupId, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerHourly)
            .AddBase(GameTimerReference.Ps5GroupId, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerHourly)
            .AddBase(GameTimerReference.Ps5GroupId, PlayMode.Online, null, GameTimerReference.Ps5OnlineHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, GameTimerReference.Ps5GroupId, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, GameTimerReference.Ps5GroupId, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, GameTimerReference.Ps5GroupId, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, GameTimerReference.Ps5GroupId, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerSpecialHourly)
            .AddBandRate(GameTimerReference.SpecialBandId, GameTimerReference.Ps5GroupId, PlayMode.Online, null, GameTimerReference.Ps5OnlineSpecialHourly)
            .AddBand(PcNormalSpecialHours)
            .AddBase(PcNormalGroupId, PlayMode.Offline, 1, PcNormalHourly)
            .AddBandRate(PcNormalBandId, PcNormalGroupId, PlayMode.Offline, 1, PcNormalSpecialHourly)
            .AddBand(PcVipSpecialHours)
            .AddBase(PcVipGroupId, PlayMode.Offline, 1, PcVipHourly)
            .AddBandRate(PcVipBandId, PcVipGroupId, PlayMode.Offline, 1, PcVipSpecialHourly)
            .Build();
    }
}
