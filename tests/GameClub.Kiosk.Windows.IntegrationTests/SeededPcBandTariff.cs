#nullable disable
using System;
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests;

/// <summary>
/// Test-side oracle for the kiosk "remaining" line on a pc-normal station. It reads the SEEDED
/// tariff (PilotSeed constants, or the TestHost harness window when the live server runs with
/// --pc-band) and does its own arithmetic. It never calls the billing engine.
/// Contract mirrored from the product: the server converts the whole wallet at the hourly rate in
/// force at the display instant (RemainingTimeEstimator: "rate in force at at only"), in whole
/// minutes. A funded period that runs past 08:30 or 00:00 is therefore still priced at the start
/// band's rate; <see cref="FundedPeriodCrossesBandEdge"/> reports that case explicitly.
/// </summary>
internal sealed class SeededPcBandTariff
{
    public const string PilotSeedSource = "pilot-seed";

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);

    public SeededPcBandTariff(string source, long baseHourlyToman, long bandHourlyToman, TimeSpan bandStart, TimeSpan bandEnd)
    {
        if (baseHourlyToman <= 0 || bandHourlyToman <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseHourlyToman), "Hourly rates must be positive.");
        }

        if (bandStart < TimeSpan.Zero || bandStart >= OneDay || bandEnd < TimeSpan.Zero || bandEnd >= OneDay)
        {
            throw new ArgumentOutOfRangeException(nameof(bandStart), "Band edges must be a time of day.");
        }

        Source = source;
        BaseHourlyToman = baseHourlyToman;
        BandHourlyToman = bandHourlyToman;
        BandStart = bandStart;
        BandEnd = bandEnd;
    }

    public string Source { get; }

    public long BaseHourlyToman { get; }

    public long BandHourlyToman { get; }

    public TimeSpan BandStart { get; }

    public TimeSpan BandEnd { get; }

    /// <summary>The seeded pc-normal tariff the installed server loads (PilotSeed.CreateCard).</summary>
    public static SeededPcBandTariff FromPilotSeed() => new(
        PilotSeedSource,
        PilotSeed.PcNormalHourly,
        PilotSeed.PcNormalSpecialHourly,
        ToSpan(PilotSeed.PcNormalSpecialHours.Start),
        ToSpan(PilotSeed.PcNormalSpecialHours.End));

    /// <summary>Seeded rates with the TestHost harness band window (HH:mm Tehran).</summary>
    public static SeededPcBandTariff FromHarnessWindow(string startText, string endText) => new(
        $"harness {startText}-{endText}",
        PilotSeed.PcNormalHourly,
        PilotSeed.PcNormalSpecialHourly,
        ToSpan(PcSpecialBandLiveRateCardHelperNet48Mirror.ParseTehranTime(startText)),
        ToSpan(PcSpecialBandLiveRateCardHelperNet48Mirror.ParseTehranTime(endText)));

    /// <summary>
    /// The tariff the live server under test uses: the TestHost harness window when it was started
    /// with band args (kiosk integration CI), otherwise the production seed (installer acceptance).
    /// </summary>
    public static SeededPcBandTariff ForLiveServer(string hostArgs, string bandMode)
    {
        var night = string.Equals(bandMode?.Trim(), "night", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(hostArgs) && !night)
        {
            return FromPilotSeed();
        }

        var (start, end) = PcSpecialBandLiveRateCardHelperNet48Mirror.ResolvePcNormalHarnessWindow(hostArgs, bandMode?.Trim());
        return FromHarnessWindow(start, end);
    }

    public static SeededPcBandTariff ForLiveServerFromEnvironment() => ForLiveServer(
        Environment.GetEnvironmentVariable("GAMECLUB_SERVER_HOST_ARGS"),
        Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_PC_BAND"));

    /// <summary>Band membership, start inclusive, end exclusive, wrapping midnight when end &lt;= start.</summary>
    public bool InBand(TimeSpan timeOfDay)
    {
        var t = Normalize(timeOfDay);
        if (BandStart == BandEnd)
        {
            return false;
        }

        return BandStart < BandEnd
            ? t >= BandStart && t < BandEnd
            : t >= BandStart || t < BandEnd;
    }

    public long HourlyAt(DateTime tehranLocal) => InBand(tehranLocal.TimeOfDay) ? BandHourlyToman : BaseHourlyToman;

    /// <summary>Whole funded minutes at the rate in force at <paramref name="tehranLocal"/>.</summary>
    public long FundedMinutes(long walletToman, DateTime tehranLocal)
    {
        if (walletToman <= 0)
        {
            return 0;
        }

        return checked(walletToman * 60L) / HourlyAt(tehranLocal);
    }

    public int ExpectedRemainingSeconds(long walletToman, DateTime tehranLocal) =>
        checked((int)(FundedMinutes(walletToman, tehranLocal) * 60L));

    public DateTime FundedEnd(long walletToman, DateTime tehranLocal) =>
        tehranLocal.AddMinutes(FundedMinutes(walletToman, tehranLocal));

    /// <summary>The next band edge (start or end) strictly after <paramref name="tehranLocal"/>.</summary>
    public DateTime NextBandEdgeAfter(DateTime tehranLocal)
    {
        var day = tehranLocal.Date;
        DateTime best = DateTime.MaxValue;
        foreach (var edge in new[] { BandStart, BandEnd })
        {
            var candidate = day + edge;
            if (candidate <= tehranLocal)
            {
                candidate = candidate.AddDays(1);
            }

            if (candidate < best)
            {
                best = candidate;
            }
        }

        return best;
    }

    public bool FundedPeriodCrossesBandEdge(long walletToman, DateTime tehranLocal) =>
        BandStart != BandEnd && NextBandEdgeAfter(tehranLocal) < FundedEnd(walletToman, tehranLocal);

    public string Describe(long walletToman, DateTime tehranLocal) =>
        $"tariff={Source} base={BaseHourlyToman} band={BandHourlyToman} window={Fmt(BandStart)}-{Fmt(BandEnd)} "
        + $"at_tehran={tehranLocal:yyyy-MM-dd HH:mm:ss} in_band={InBand(tehranLocal.TimeOfDay)} hourly={HourlyAt(tehranLocal)} "
        + $"expected_seconds={ExpectedRemainingSeconds(walletToman, tehranLocal)} funded_end={FundedEnd(walletToman, tehranLocal):HH:mm} "
        + $"crosses_edge={FundedPeriodCrossesBandEdge(walletToman, tehranLocal)}";

    private static TimeSpan ToSpan(TimeOfDay time) => TimeSpan.FromSeconds(time.SecondOfDay);

    private static TimeSpan Normalize(TimeSpan t)
    {
        var ticks = t.Ticks % OneDay.Ticks;
        return TimeSpan.FromTicks(ticks < 0 ? ticks + OneDay.Ticks : ticks);
    }

    private static string Fmt(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";
}
