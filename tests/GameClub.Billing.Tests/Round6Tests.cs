using GameClub.Billing;

namespace GameClub.Billing.Tests;

public class Round6Tests
{
    [Fact]
    public void Flat_block_grace_across_midnight_is_not_overshoot()
    {
        var card = Flat();
        var start = T.At(2026, 6, 2, 23, 50);
        var five = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan5 = PrepaidPlanner.ForAmount(start, 18_833, "a", "st", "g", PlayMode.Offline, 1, card, policy: five);
        Assert.Equal(T.At(2026, 6, 3, 0, 1), plan5.End);
        Assert.Equal(500, plan5.UnusedToman);
        Assert.NotNull(plan5.Grace);
        var played5 = Price(card, start, plan5.End, five);
        var settled5 = AccountSettlement.Settle(played5, new PcAccount("m", 18_833, []), billing: five);
        Assert.Equal(plan5.ConsumedToman, settled5.MoneyConsumedToman);
        Assert.Equal(plan5.Grace!.WaivedToman, settled5.Grace!.WaivedToman);
        Assert.Equal(500, settled5.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(0, settled5.WriteOffToman);
        Assert.Null(settled5.Alert);

        var fifteen = new BillingPolicy(15, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var plan15 = PrepaidPlanner.ForAmount(start, 55_500, "a", "st", "g", PlayMode.Offline, 1, card, policy: fifteen);
        Assert.Equal(500, plan15.UnusedToman);
        Assert.NotNull(plan15.Grace);
        var settled15 = AccountSettlement.Settle(Price(card, start, plan15.End, fifteen), new PcAccount("m", 55_500, []), billing: fifteen);
        Assert.Equal(0, settled15.WriteOffToman);
        Assert.Null(settled15.Alert);
        Assert.Equal(500, settled15.UpdatedAccount.MoneyBalanceToman);
        Assert.Equal(plan15.Grace!.WaivedToman, settled15.Grace!.WaivedToman);
    }

    [Fact]
    public void A_code_window_edge_mid_session_discounts_only_the_minutes_inside_it()
    {
        var card = Flat();
        var half = DiscountCode.PercentOff("HALF", 50);
        var untilEleven = half with { ValidUntil = T.At(2026, 6, 2, 11, 0) };
        var fromEleven = half with { ValidFrom = T.At(2026, 6, 2, 11, 0) };
        var twoHours = Play(card, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 12, 0));
        Assert.Equal(55_000, CustomerPlay.Settle(twoHours, Wallet(), code: untilEleven).Settlement.DiscountToman);
        Assert.Equal(55_000, CustomerPlay.Settle(twoHours, Wallet(), code: fromEleven).Settlement.DiscountToman);

        var until2330 = half with { ValidUntil = T.At(2026, 6, 2, 23, 30) };
        var from1030 = half with { ValidFrom = T.At(2026, 6, 2, 10, 30) };
        var night = Play(card, T.At(2026, 6, 2, 23, 0), T.At(2026, 6, 3, 1, 0));
        var morning = Play(card, T.At(2026, 6, 2, 10, 0), T.At(2026, 6, 2, 11, 0));
        Assert.Equal(27_500, CustomerPlay.Settle(night, Wallet(), code: until2330, issuedAt: T.At(2026, 6, 3, 1, 0)).Settlement.DiscountToman);
        Assert.Equal(27_500, CustomerPlay.Settle(morning, Wallet(), code: from1030).Settlement.DiscountToman);
    }

    [Fact]
    public void Grace_across_midnight_uses_the_whole_run_marginal()
    {
        var card = Flat();
        var start = T.At(2026, 6, 2, 23, 55);
        foreach (var budget in new long[] { 12_834, 18_334, 23_834 })
        {
            var plan = PrepaidPlanner.ForAmount(start, budget, "a", "st", "g", PlayMode.Offline, 1, card);
            var settled = AccountSettlement.Settle(Price(card, start, plan.End, null), new PcAccount("m", budget, []));
            Assert.Equal(plan.ConsumedToman, settled.MoneyConsumedToman);
            Assert.Equal(plan.Grace?.WaivedToman ?? 0, settled.Grace?.WaivedToman ?? 0);
            Assert.NotNull(settled.Grace);
            Assert.Equal(0, settled.WriteOffToman);
            Assert.Null(settled.Alert);
            Assert.Equal(0, settled.UncoveredToman);
        }
    }

    [Fact]
    public void A_kind_conflict_is_rejected_with_no_pause()
    {
        var stations = new StationDirectory(
            [new StationGroup(PilotSeed.PcNormalGroupId, "PC", StationKind.Pc)],
            [new Station("pc1", "PC 1", PilotSeed.PcNormalGroupId)]);
        var start = T.At(2026, 6, 2, 10, 0);
        var session = new Session("pc", [
            new PlaySegment("a", start, start.AddMinutes(10), "pc1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Console),
        ]);
        var error = Assert.Throws<BillingException>(() => SessionPricer.Price(session, PilotSeed.CreateCard(), stations: stations));
        Assert.Equal(BillingErrors.InvalidSession, error.Code);
    }

    [Fact]
    public void Planner_and_settlement_match_a_per_minute_oracle()
    {
        var flat = Flat();
        var banded = new RateCardBuilder()
            .AddBase("g", PlayMode.Offline, 1, 110_000)
            .AddBand(new TimeBand("night", "g", "night", new TimeOfDay(0, 0), new TimeOfDay(8, 0)))
            .AddBandRate("night", "g", PlayMode.Offline, 1, 160_000)
            .Build();
        var rng = new Random(6);
        foreach (var granularity in new[] { 1, 5, 15 })
        {
            var policy = new BillingPolicy(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
            foreach (var card in new[] { flat, banded })
            {
                for (var n = 0; n < 8; n++)
                {
                    var start = TehranTime.AddSeconds(T.At(2026, 6, 2, 22, 0), rng.Next(0, 4 * 3600));
                    var budget = rng.Next(1, 40_000);
                    var plan = PrepaidPlanner.ForAmount(start, budget, "a", "st", "g", PlayMode.Offline, 1, card, policy: policy);
                    if (plan.IsUnlimited || plan.End <= plan.Start)
                    {
                        continue;
                    }

                    var charge = Price(card, plan.Start, plan.End, policy);
                    var settled = AccountSettlement.Settle(charge, new PcAccount("m", budget, []), billing: policy);
                    Assert.Equal(plan.ConsumedToman, settled.MoneyConsumedToman);
                    Assert.Equal(plan.Grace?.WaivedToman ?? 0, settled.Grace?.WaivedToman ?? 0);
                    Assert.Equal(0, settled.UncoveredToman);
                    var playedPast = charge.TotalActiveSeconds > (plan.Grace is null ? plan.Lines.Sum(line => line.BilledMinutes) : plan.Lines.Sum(line => line.BilledMinutes)) * 60L;
                    if (!playedPast)
                    {
                        Assert.Equal(0, settled.WriteOffToman);
                        Assert.Null(settled.Alert);
                    }
                }
            }
        }

        var edge = rng.Next(1, 50);
        var windowStart = T.At(2026, 6, 2, 10, 0);
        var code = DiscountCode.PercentOff("W", 50, validUntil: windowStart.AddMinutes(edge));
        var session = Play(flat, windowStart, windowStart.AddHours(1));
        long eligible = 0;
        for (long minute = 0; minute < session.TotalBilledMinutes; minute++)
        {
            var at = TehranTime.AddSeconds(windowStart, minute * 60L);
            if (at < code.ValidUntil)
            {
                eligible += MoneyMath.ChargeForMinutes(110_000, minute + 1, RateRemainderMode.Nearest)
                    - MoneyMath.ChargeForMinutes(110_000, minute, RateRemainderMode.Nearest);
            }
        }

        var paid = CustomerPlay.Settle(session, Wallet(), code: code);
        Assert.Equal(MoneyMath.PercentOf(eligible, 50), paid.Settlement.DiscountToman);
        Assert.Equal(paid.Settlement.MoneyConsumedToman, paid.ChargeInvoice!.PayableToman);
    }

    private static RateCard Flat() => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    private static SessionCharge Price(RateCard card, DateTimeOffset start, DateTimeOffset end, BillingPolicy? policy)
        => SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1),
        ]), card, policy: policy);

    private static SessionCharge Play(RateCard card, DateTimeOffset start, DateTimeOffset end) => Price(card, start, end, null);

    private static CustomerAccount Wallet()
        => new("m", WalletSettings.Shared with { ApplyDiscountsToPc = true }, new WalletPocket(1_000_000, 0), new WalletPocket(0, 0), []);
}
