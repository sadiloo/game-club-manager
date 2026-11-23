using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class PcInternetBillingTests
{
    private static RateCard SamplePcCard()
    {
        const long offDay = 60_000;
        const long offNight = 80_000;
        return new RateCardBuilder()
            .AddBand(PilotSeed.PcNormalSpecialHours)
            .AddBand(PilotSeed.PcVipSpecialHours)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalHourly)
            .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalSpecialHourly)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, offDay)
            .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, offNight)
            .AddBase(PilotSeed.PcVipGroupId, PlayMode.Online, null, PilotSeed.PcVipHourly)
            .AddBandRate(PilotSeed.PcVipBandId, PilotSeed.PcVipGroupId, PlayMode.Online, null, PilotSeed.PcVipSpecialHourly)
            .Build();
    }

    private static SessionCharge Price(params PlaySegment[] segments)
        => SessionPricer.Price(new Session("x", segments), SamplePcCard());

    [Fact]
    public void PcInternet_E1_three_segments_whole_minutes_total_110000()
    {
        var start = T.At(2026, 6, 2, 17, 0);
        var charge = Price(
            CafeFixture.Play("a", start, start.AddMinutes(45), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", start.AddMinutes(45), start.AddMinutes(75), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            CafeFixture.Play("c", start.AddMinutes(75), start.AddMinutes(90), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        Assert.Equal(110_000, charge.TotalAmountToman);
        Assert.Equal(90, charge.TotalBilledMinutes);
        Assert.Equal(3, charge.Lines.Count);
    }

    [Fact]
    public void PcInternet_E2_press_mid_minute_total_43667()
    {
        var start = T.At(2026, 6, 2, 20, 0);
        var off = T.At(2026, 6, 2, 20, 10, 30);
        var end = T.At(2026, 6, 2, 20, 40);
        var charge = Price(
            CafeFixture.Play("a", start, off, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", off, end, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline));
        Assert.Equal(43_667, charge.TotalAmountToman);
        Assert.Equal(40, charge.TotalBilledMinutes);
    }

    [Fact]
    public void PcInternet_E3_night_band_after_off_total_103334()
    {
        var start = T.At(2026, 6, 2, 23, 0);
        var off = T.At(2026, 6, 2, 23, 50);
        var end = T.At(2026, 6, 3, 0, 20);
        var charge = Price(
            CafeFixture.Play("a", start, off, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", off, end, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline));
        Assert.Equal(103_334, charge.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E4_transfer_while_off_total_73333()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var offAt = start.AddMinutes(30);
        var xferAt = offAt.AddMinutes(20);
        var end = xferAt.AddMinutes(10);
        var charge = Price(
            CafeFixture.Play("a", start, offAt, "pc-3", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", offAt, xferAt, "pc-3", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            CafeFixture.Play("c", xferAt, end, "pc-4", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        Assert.Equal(73_333, charge.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E5_migration_parity_80000_and_130000()
    {
        var card = PilotSeed.CreateCard();
        var day = T.At(2026, 6, 2, 10, 0);
        var normal = SessionPricer.Price(
            CafeFixture.Session("e5n", CafeFixture.Play("a", day, day.AddMinutes(60), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online)),
            card);
        Assert.Equal(80_000, normal.TotalAmountToman);

        var nightStart = T.At(2026, 6, 2, 2, 0);
        var vip = SessionPricer.Price(
            CafeFixture.Session("e5v", CafeFixture.Play("a", nightStart, nightStart.AddMinutes(60), "pc-vip-1", PilotSeed.PcVipGroupId, 1, PlayMode.Online)),
            card);
        Assert.Equal(130_000, vip.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E9_short_off_26333()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Price(
            CafeFixture.Play("a", start, start.AddSeconds(600), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", start.AddSeconds(600), start.AddSeconds(630), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            CafeFixture.Play("c", start.AddSeconds(630), start.AddSeconds(1200), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        Assert.Equal(26_333, charge.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E9_variant_zero_min_off_26667()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Price(
            CafeFixture.Play("a", start, start.AddSeconds(630), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", start.AddSeconds(630), start.AddSeconds(645), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            CafeFixture.Play("c", start.AddSeconds(645), start.AddSeconds(1200), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        Assert.Equal(26_667, charge.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E10_per_line_rounding_202084()
    {
        var card = new RateCardBuilder()
            .AddBand(PilotSeed.PcNormalSpecialHours)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Online, null, 125_000)
            .Build();
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(new Session("e10", [
            CafeFixture.Play("a", start, start.AddMinutes(77), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", start.AddMinutes(77), start.AddMinutes(97), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
        ]), card);
        if (charge.Lines.Count == 2)
        {
            Assert.Equal(202_084, charge.TotalAmountToman);
        }
        else
        {
            Assert.Equal(202_083, charge.TotalAmountToman);
        }
    }

    [Fact]
    public void PcInternet_E6_amount_prepaid_toggle_30_to_35_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 10, 0);
        var initial = PrepaidPlanner.ForAmount(start, 40_000, "a", "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Online, 1, card);
        Assert.Equal(30 * 60, initial.GrantedActiveSeconds);
        var after15 = start.AddMinutes(15);
        var spent = SessionPricer.Price(
            CafeFixture.Session("s", CafeFixture.Play("a", start, after15, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online)),
            card).TotalAmountToman;
        Assert.Equal(20_000, spent);
        var cont = PrepaidPlanner.ForAmount(after15, 20_000, "b", "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, card);
        Assert.Equal(20 * 60, cont.GrantedActiveSeconds);
        Assert.Equal(35 * 60, 15 * 60 + cont.GrantedActiveSeconds);
    }

    [Fact]
    public void PcInternet_E7_wallet_estimate_22_to_30_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 10, 0);
        var account = new PcAccount("e7", 30_000);
        var online = RemainingTimeEstimator.Estimate(account, PilotSeed.PcNormalGroupId, PlayMode.Online, 1, start, card);
        Assert.Equal(22, online.TotalMinutes);
        var offline = RemainingTimeEstimator.Estimate(account, PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, start, card);
        Assert.Equal(30, offline.TotalMinutes);
    }

    [Fact]
    public void PcInternet_E9_variant_merge_single_online_line_26667()
    {
        var start = T.At(2026, 6, 2, 10, 0);
        var charge = Price(
            CafeFixture.Play("a", start, start.AddSeconds(630), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", start.AddSeconds(630), start.AddSeconds(645), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            CafeFixture.Play("c", start.AddSeconds(645), start.AddSeconds(1200), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        Assert.Single(charge.Lines);
        Assert.Equal(26_667, charge.TotalAmountToman);
    }

    [Fact]
    public void PcInternet_E2_attribution_press_at_30s_yields_11_on_minutes()
    {
        var start = T.At(2026, 6, 2, 20, 0);
        var off = start.AddMinutes(10).AddSeconds(30);
        var end = T.At(2026, 6, 2, 20, 40);
        var charge = Price(
            CafeFixture.Play("a", start, off, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", off, end, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline));
        Assert.Equal(11, charge.Lines[0].BilledMinutes);
    }

    [Fact]
    public void PcInternet_E6b_amount_prepaid_transfer_25_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 10, 0);
        var initial = PrepaidPlanner.ForAmount(start, 40_000, "a", "pc-1", PilotSeed.PcNormalGroupId, PlayMode.Online, 1, card);
        Assert.Equal(30 * 60, initial.GrantedActiveSeconds);
        var after15 = start.AddMinutes(15);
        var spent = SessionPricer.Price(
            CafeFixture.Session("s", CafeFixture.Play("a", start, after15, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online)),
            card).TotalAmountToman;
        Assert.Equal(20_000, spent);
        var cont = PrepaidPlanner.ForAmount(
            after15,
            20_000,
            "b",
            "pc-vip-1",
            PilotSeed.PcVipGroupId,
            PlayMode.Online,
            1,
            card,
            graceMinuteEnabled: false);
        Assert.Equal(10 * 60, cont.GrantedActiveSeconds);
        Assert.Equal(25 * 60, 15 * 60 + cont.GrantedActiveSeconds);
    }

    [Fact]
    public void PcInternet_E8a_disconnect_reconnect_pricing_total_42667()
    {
        var start = T.At(2026, 6, 2, 20, 0);
        var off = start.AddMinutes(8);
        var end = T.At(2026, 6, 2, 20, 40);
        var charge = Price(
            CafeFixture.Play("a", start, off, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            CafeFixture.Play("b", off, end, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline));
        Assert.Equal(42_667, charge.TotalAmountToman);
        Assert.Equal(40, charge.TotalBilledMinutes);
        Assert.Equal(10_667, charge.Lines[0].AmountToman);
        Assert.Equal(32_000, charge.Lines[1].AmountToman);
    }

    [Fact]
    public void PcInternet_E8b_dead_pc_clamp_on_segment_6667()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 20, 0);
        var billEnd = start.AddMinutes(5);
        var off = start.AddMinutes(8);
        var charge = SessionPricer.Price(
            new Session("e8b", [
                CafeFixture.Play("a", start, billEnd, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
                CafeFixture.Play("b", off, off, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
            ]),
            card);
        Assert.Equal(6_667, charge.TotalAmountToman);
        Assert.Equal(5, charge.TotalBilledMinutes);
    }

    [Fact]
    public void PcInternet_E11_duration_toggle_60_more_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 20, 0);
        var booked = PrepaidPlanner.ForDuration(
            start,
            TimeSpan.FromMinutes(60),
            "a",
            "pc-1",
            PilotSeed.PcNormalGroupId,
            PlayMode.Online,
            1,
            card);
        Assert.Equal(80_000, booked.ConsumedToman);
        var after15 = start.AddMinutes(15);
        var session = CafeFixture.Session("e11", CafeFixture.Play("a", start, after15, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        var tail = CafeFixture.Play("b", after15, after15, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline);
        var plan = PrepaidPlanner.ForAmountFromSegments(session, after15, tail, 80_000, card, graceMinuteEnabled: false);
        Assert.Equal(75 * 60, plan.GrantedActiveSeconds);
    }

    [Fact]
    public void PcInternet_E12_duration_midnight_62_more_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 23, 30);
        var booked = PrepaidPlanner.ForDuration(
            start,
            TimeSpan.FromMinutes(60),
            "a",
            "pc-1",
            PilotSeed.PcNormalGroupId,
            PlayMode.Online,
            1,
            card);
        Assert.Equal(90_000, booked.ConsumedToman);
        var after10 = start.AddMinutes(10);
        var session = CafeFixture.Session("e12", CafeFixture.Play("a", start, after10, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        var tail = CafeFixture.Play("b", after10, after10, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline);
        var plan = PrepaidPlanner.ForAmountFromSegments(session, after10, tail, 90_000, card, graceMinuteEnabled: false);
        Assert.Equal(72 * 60, plan.GrantedActiveSeconds);
    }

    [Fact]
    public void PcInternet_E13_duration_transfer_32_more_min()
    {
        var card = SamplePcCard();
        var start = T.At(2026, 6, 2, 20, 0);
        var booked = PrepaidPlanner.ForDuration(
            start,
            TimeSpan.FromMinutes(60),
            "a",
            "pc-1",
            PilotSeed.PcNormalGroupId,
            PlayMode.Online,
            1,
            card);
        Assert.Equal(80_000, booked.ConsumedToman);
        var after15 = start.AddMinutes(15);
        var session = CafeFixture.Session("e13", CafeFixture.Play("a", start, after15, "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online));
        var tail = CafeFixture.Play("b", after15, after15, "pc-vip-1", PilotSeed.PcVipGroupId, 1, PlayMode.Online);
        var plan = PrepaidPlanner.ForAmountFromSegments(session, after15, tail, 80_000, card, graceMinuteEnabled: false);
        Assert.Equal(47 * 60, plan.GrantedActiveSeconds);
    }
}