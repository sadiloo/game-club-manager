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
    public void assert_branch_is_single_when_no_edge_between_even_if_funded_period_crosses_08_30()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var login = Tehran(27, 8, 10);
        var capture = Tehran(27, 8, 11, 30);
        Assert.True(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, login));
        Assert.Null(tariff.EdgeBetween(login, capture));
        Assert.Equal(SeededPcBandTariff.BranchSingle, tariff.RemainingTimeAssertBranch(FundedWalletToman, login, capture));
    }

    [Fact]
    public void assert_branch_is_single_when_no_edge_between_even_if_funded_period_crosses_00_00()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var login = Tehran(26, 23, 50);
        var capture = Tehran(26, 23, 51, 30);
        Assert.True(tariff.FundedPeriodCrossesBandEdge(FundedWalletToman, login));
        Assert.Null(tariff.EdgeBetween(login, capture));
        Assert.Equal(SeededPcBandTariff.BranchSingle, tariff.RemainingTimeAssertBranch(FundedWalletToman, login, capture));
    }

    [Fact]
    public void assert_branch_is_edge_window_when_08_30_lies_between_login_and_capture()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var login = Tehran(27, 8, 29, 40);
        var capture = Tehran(27, 8, 30, 20);
        Assert.Equal(Tehran(27, 8, 30), tariff.EdgeBetween(login, capture));
        Assert.Equal(SeededPcBandTariff.BranchEdgeWindow, tariff.RemainingTimeAssertBranch(FundedWalletToman, login, capture));
    }

    [Fact]
    public void assert_branch_is_edge_window_when_00_00_lies_between_login_and_capture()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        var login = Tehran(26, 23, 59, 30);
        var capture = Tehran(27, 0, 0, 45);
        Assert.Equal(Tehran(27, 0, 0), tariff.EdgeBetween(login, capture));
        Assert.Equal(SeededPcBandTariff.BranchEdgeWindow, tariff.RemainingTimeAssertBranch(FundedWalletToman, login, capture));
    }

    [Fact]
    public void assert_branch_edge_interval_is_open_at_login_and_closed_at_capture()
    {
        var tariff = SeededPcBandTariff.FromPilotSeed();
        // capture exactly at the edge: (login, capture] contains it
        Assert.Equal(Tehran(27, 8, 30), tariff.EdgeBetween(Tehran(27, 8, 29, 59), Tehran(27, 8, 30, 0)));
        Assert.Equal(SeededPcBandTariff.BranchEdgeWindow, tariff.RemainingTimeAssertBranch(FundedWalletToman, Tehran(27, 8, 29, 59), Tehran(27, 8, 30, 0)));
        Assert.Equal(Tehran(27, 0, 0), tariff.EdgeBetween(Tehran(26, 23, 59, 59), Tehran(27, 0, 0, 0)));
        // login exactly at the edge: the edge is not after login, so no edge between
        Assert.Null(tariff.EdgeBetween(Tehran(27, 8, 30, 0), Tehran(27, 8, 31, 0)));
        Assert.Equal(SeededPcBandTariff.BranchSingle, tariff.RemainingTimeAssertBranch(FundedWalletToman, Tehran(27, 8, 30, 0), Tehran(27, 8, 31, 0)));
        // login == capture: empty interval
        Assert.Null(tariff.EdgeBetween(Tehran(27, 8, 30, 0), Tehran(27, 8, 30, 0)));
    }

    [Fact]
    public void assert_branch_follows_the_edge_not_the_rate_difference()
    {
        // The branch must come from an edge between login and capture, not from comparing the two expected values.
        // Equal band and day rates: an edge lies between, the values are equal -> still edge_window.
        var flat = new SeededPcBandTariff("flat", 80_000, 80_000, TimeSpan.Zero, new TimeSpan(8, 30, 0));
        Assert.Equal(
            flat.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 8, 29, 40)),
            flat.ExpectedRemainingSeconds(FundedWalletToman, Tehran(27, 8, 30, 20)));
        Assert.Equal(SeededPcBandTariff.BranchEdgeWindow, flat.RemainingTimeAssertBranch(FundedWalletToman, Tehran(27, 8, 29, 40), Tehran(27, 8, 30, 20)));

        // Both edges between (same band at both ends, equal values) -> edge_window, edge = the first one.
        var seeded = SeededPcBandTariff.FromPilotSeed();
        Assert.Equal(Tehran(27, 0, 0), seeded.EdgeBetween(Tehran(26, 23, 59), Tehran(27, 8, 31)));
        Assert.Equal(SeededPcBandTariff.BranchEdgeWindow, seeded.RemainingTimeAssertBranch(FundedWalletToman, Tehran(26, 23, 59), Tehran(27, 8, 31)));

        // A band with start == end never has an edge.
        var none = new SeededPcBandTariff("none", 80_000, 100_000, new TimeSpan(5, 0, 0), new TimeSpan(5, 0, 0));
        Assert.Null(none.EdgeBetween(Tehran(27, 4, 59), Tehran(27, 5, 1)));
    }

    [Fact]
    public void one_tick_tolerance_is_exactly_one_second()
    {
        // QA M7: a ±500 s (or any wider) tolerance must fail here.
        Assert.Equal(1, RemainingTimeOneTick.ToleranceSeconds);
        Assert.Equal(1_739, RemainingTimeOneTick.Low(1_740));
        Assert.Equal(1_741, RemainingTimeOneTick.High(1_740));
        Assert.True(RemainingTimeOneTick.Within(2_160, 2_159));
        Assert.True(RemainingTimeOneTick.Within(2_160, 2_160));
        Assert.True(RemainingTimeOneTick.Within(2_160, 2_161));
        Assert.False(RemainingTimeOneTick.Within(2_160, 2_158));
        Assert.False(RemainingTimeOneTick.Within(2_160, 2_162));
        // the R10k installer failure: displayed 1740 against a day-rate 2160 must never pass
        Assert.False(RemainingTimeOneTick.Within(2_160, 1_740));
        Assert.False(RemainingTimeOneTick.Within(2_160, 2_660));
    }

    [Fact]
    public void wpf_remaining_time_tests_take_their_tolerance_from_the_one_tick_helper()
    {
        // QA M7 source pin: the WPF tests (net48, not runnable here) must not carry their own tolerance literal.
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "GameClub.linux.slnf")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            dir!.FullName, "tests", "GameClub.Kiosk.Windows.IntegrationTests", "Wpf", "WindowsKioskWpfSmokeTests.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            source,
            System.Text.RegularExpressions.Regex.Escape("Assert.InRange(displayedSeconds, RemainingTimeOneTick.Low(expectedSeconds), RemainingTimeOneTick.High(expectedSeconds));")).Count);
        Assert.Contains("RemainingTimeOneTick.Within(expectedSeconds, displayedSeconds)", source, StringComparison.Ordinal);
        Assert.Contains("RemainingTimeOneTick.Within(expectedAtCapture, displayedSeconds)", source, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(source, @"Assert\.InRange\(displayedSeconds").Count);
        Assert.DoesNotMatch(@"(expectedSeconds|expectedAtCapture)\s*[-+]\s*\d", source);
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
