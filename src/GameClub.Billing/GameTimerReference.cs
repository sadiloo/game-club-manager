namespace GameClub.Billing;

/// <summary>
/// Rates read from the owner's Game Timer 4.3 workbook (sheet <c>nerkh</c>, rows 17–21).
/// Per-minute cells in that sheet are <c>hourly / 60</c> as floating point. This library
/// does not store them. VR, PC, Xbox, VIP, and the timed tables (billiard, ping-pong,
/// foosball, hockey) were present with an hourly price of 0 or blank — those rates are
/// unknown, not free, and are intentionally absent so a lookup fails loudly.
/// Game Timer rounds the game charge with <c>ROUND(amount, -2)</c> (nearest 100 toman).
/// That step is not the default here; branch rounding is opt-in and starts from the
/// PA-8 steps (1,000 / 5,000 / 10,000 or custom).
/// </summary>
public static class GameTimerReference
{
    public const string Ps5GroupId = "ps5";

    public const string SpecialBandId = "special-hours";

    public static readonly TimeOfDay SpecialStart = new(0, 0);

    public static readonly TimeOfDay SpecialEnd = new(8, 30);

    public const long Ps5OnePlayerHourly = 110_000;
    public const long Ps5TwoPlayerHourly = 125_000;
    public const long Ps5ThreePlayerHourly = 160_000;
    public const long Ps5FourPlayerHourly = 220_000;
    public const long Ps5OnlineHourly = 170_000;

    public const long Ps5OnePlayerSpecialHourly = 160_000;
    public const long Ps5TwoPlayerSpecialHourly = 175_000;
    public const long Ps5ThreePlayerSpecialHourly = 210_000;
    public const long Ps5FourPlayerSpecialHourly = 270_000;
    public const long Ps5OnlineSpecialHourly = 220_000;

    /// <summary>Buffet sheet service ABC_1, "خسارت انداختن دسته".</summary>
    public const long ControllerDamageFineToman = 900_000;

    /// <summary>
    /// Optional example ladder (Bronze 5% … Titan 28%) with placeholder hour thresholds.
    /// The customer sheet had the columns but no rows. Edit it; do not treat the hours as live data.
    /// </summary>
    public static LevelLadder ExampleLevelSeed => LevelLadder.ExampleSeed;

    public static TimeBand SpecialHours { get; } = new(SpecialBandId, Ps5GroupId, "Special hours 00:00–08:30", SpecialStart, SpecialEnd);

    public static RateCard CreatePs5Card()
    {
        return new RateCardBuilder()
            .AddBand(SpecialHours)
            .AddBase(Ps5GroupId, PlayMode.Offline, 1, Ps5OnePlayerHourly)
            .AddBase(Ps5GroupId, PlayMode.Offline, 2, Ps5TwoPlayerHourly)
            .AddBase(Ps5GroupId, PlayMode.Offline, 3, Ps5ThreePlayerHourly)
            .AddBase(Ps5GroupId, PlayMode.Offline, 4, Ps5FourPlayerHourly)
            .AddBase(Ps5GroupId, PlayMode.Online, null, Ps5OnlineHourly)
            .AddBandRate(SpecialBandId, Ps5GroupId, PlayMode.Offline, 1, Ps5OnePlayerSpecialHourly)
            .AddBandRate(SpecialBandId, Ps5GroupId, PlayMode.Offline, 2, Ps5TwoPlayerSpecialHourly)
            .AddBandRate(SpecialBandId, Ps5GroupId, PlayMode.Offline, 3, Ps5ThreePlayerSpecialHourly)
            .AddBandRate(SpecialBandId, Ps5GroupId, PlayMode.Offline, 4, Ps5FourPlayerSpecialHourly)
            .AddBandRate(SpecialBandId, Ps5GroupId, PlayMode.Online, null, Ps5OnlineSpecialHourly)
            .Build();
    }
}
