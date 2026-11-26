#if NET10_0_OR_GREATER
using System;
using GameClub.Billing;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests;

/// <summary>
/// Box/ubuntu unit tests for the test-side expected-remaining-time oracle used by the live WPF
/// remaining-time tests (installer B6-B12 and kiosk integration CI). No live server needed.
/// </summary>
public sealed class SeededPcBandTariffTests
{
    private const long FundedWalletToman = 48_667;

    private static DateTime Tehran(int day, int hour, int minute, int second = 0) =>
        new(2026, 11, day, hour, minute, second, DateTimeKind.Unspecified);

    [Fact]
    public void pilot_seed_tariff_reads_seeded_pc_normal_rates_and_window()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        Assert.Equal(SeededPcBandTariff.PilotSeedSource, tariff.Source);
        Assert.Equal(80_000, tariff.BaseHourlyToman);
        Assert.Equal(100_000, tariff.BandHourlyToman);
        Assert.Equal(TimeSpan.Zero, tariff.BandStart);
        Assert.Equal(new TimeSpan(8, 30, 0), tariff.BandEnd);
    }

    [Fact]
    public void day_band_wallet_is_priced_at_seeded_day_rate()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var at = Tehran(26, 14, 0);
        Assert.False(tariff.InBand(at.TimeOfDay));
        Assert.Equal(80_000, tariff.HourlyAt(at));
        Assert.Equal(2_160, tariff.ExpectedRemainingSeconds(FundedWalletToman, at));
        Assert.False(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, at));
    }

    [Fact]
    public void night_band_wallet_is_priced_at_seeded_special_rate()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var at = Tehran(27, 3, 0);
        Assert.True(tariff.InBand(at.TimeOfDay));
        Assert.Equal(100_000, tariff.HourlyAt(at));
        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, at));
        Assert.False(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, at));
    }

    [Fact]
    public void installer_job_140168674648_start_at_0046_tehran_expects_special_band_1740()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var at = TehranTime.FromUtc(new DateTime(2026, 11, 26, 21, 16, 42, DateTimeKind.Utc)).DateTime;
        Assert.Equal(Tehran(27, 0, 46, 42), at);
        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, at));
    }

    [Fact]
    public void funded_period_crossing_0830_keeps_start_band_rate()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var at = Tehran(27, 8, 10);
        Assert.True(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, at));
        Assert.Equal(Tehran(27, 8, 30), tariff.NextBandEdgeAfter(at));
        Assert.Equal(Tehran(27, 8, 39), tariff.FundedEnd(FundedWalletToman, at));
        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, at));

        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 8, 29, 59)));
        Assert.Equal(2_160, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 8, 30, 0)));
        Assert.True(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, Tehran(27, 8, 29, 59)));
        Assert.False(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, Tehran(27, 8, 30, 0)));
    }

    [Fact]
    public void funded_period_crossing_0000_keeps_start_band_rate()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var at = Tehran(26, 23, 50);
        Assert.True(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, at));
        Assert.Equal(Tehran(27, 0, 0), tariff.NextBandEdgeAfter(at));
        Assert.Equal(Tehran(27, 0, 26), tariff.FundedEnd(FundedWalletToman, at));
        Assert.Equal(2_160, tariff.ExpectedRemainingSeconds(FundedWalletToman, at));

        Assert.Equal(2_160, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(26, 23, 59, 59)));
        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 0, 0, 0)));
        Assert.False(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, Tehran(27, 0, 0, 0)));
    }

    [Fact]
    public void harness_night_window_wrapping_midnight_is_special_band()
    {
        var tariff = SeededPcBandTariff.FromHarnessWindow("21:00", "03:00");
        Assert.True(tariff.InBand(new TimeSpan(0, 30, 0)));
        Assert.True(tariff.InBand(new TimeSpan(21, 0, 0)));
        Assert.False(tariff.InBand(new TimeSpan(3, 0, 0)));
        Assert.Equal(1_740, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 0, 30)));
        Assert.Equal(2_160, tariff.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 12, 0)));
    }

    [Fact]
    public void oracle_agrees_with_product_estimate_on_seeded_card_every_minute_of_the_day()
    {
        // Cross-check only: the oracle is independent arithmetic on seed data; this proves it matches what the
        // server shows (RemainingTimeEstimator on PilotSeed.CreateCard) at every minute, both sides of 00:00 and 08:30.
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var card = PilotSeed.CreateCard();
        var account = new PcAccount("fund48667", FundedWalletToman);
        for (var minute = 0; minute < 24 * 60; minute++)
        {
            var tehran = TehranTime.At(2026, 11, 27, minute / 60, minute % 60);
            var product = RemainingTimeEstimator.Estimate(
                account, PilotSeed.PcNormalGroupId, PlayMode.Online, 1, tehran, card, graceMinuteEnabled: true).MoneyMinutes!.Value * 60;
            Assert.True(
                product == tariff.ExpectedRemainingSeconds(FundedWalletToman, tehran.DateTime),
                $"minute {minute}: product {product}s vs oracle {tariff.Describe(FundedWalletToman, tehran.DateTime)}");
        }
    }

    [Fact]
    public void live_server_tariff_selection_follows_band_environment()
    {
        Assert.Equal(SeededPcBandTariff.PilotSeedSource, SeededPcBandTariff.ForLiveServer(null, null).Source);
        Assert.Equal(SeededPcBandTariff.PilotSeedSource, SeededPcBandTariff.ForLiveServer("  ", " ").Source);

        var night = SeededPcBandTariff.ForLiveServer(null, "night");
        Assert.StartsWith("harness ", night.Source);
        var nowTehran = TehranTime.FromUtc(DateTime.UtcNow).DateTime;
        Assert.True(night.InBand(nowTehran.TimeOfDay));

        var day = SeededPcBandTariff.ForLiveServer("--pc-band day", null);
        Assert.StartsWith("harness ", day.Source);
        Assert.False(day.InBand(nowTehran.TimeOfDay));

        var explicitWindow = SeededPcBandTariff.ForLiveServer("--pc-normal-start 20:13 --pc-normal-end 20:43", null);
        Assert.Equal(new TimeSpan(20, 13, 0), explicitWindow.BandStart);
        Assert.Equal(new TimeSpan(20, 43, 0), explicitWindow.BandEnd);
    }
}
#endif
