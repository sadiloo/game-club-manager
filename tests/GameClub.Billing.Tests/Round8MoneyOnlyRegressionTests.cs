using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round8MoneyOnlyRegressionTests
{
    static BillingPolicy G(int granularity)
        => new(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    static RateCard Flat()
        => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    static long SettlementSum(Settlement st)
        => st.MoneyConsumedToman + st.DiscountToman
            + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

    [Fact]
    public void rc_206_tail_grace_waived_toman_1833_money_only()
    {
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddHours(1), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: G(1));
        var st = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 56_000),
            G(1),
            discount: new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 2, 22, 40)));
        Assert.Equal(charge.TotalAmountToman, SettlementSum(st));
        // B5': money-only on 1a731de engine with [] — full discounted net of grace minute.
        Assert.Equal(T.At(2026, 6, 2, 22, 30), st.Grace?.At);
        Assert.Equal(1_833, st.Grace!.WaivedToman);
    }

    [Fact]
    public void rc_207_one_toman_short_does_not_buy_next_minute_or_write_off()
    {
        const string V = PilotSeed.PcVipGroupId;
        var start = T.At(2026, 6, 2, 23, 50);
        var end = T.At(2026, 6, 3, 0, 20);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, end, "st", V, PlayMode.Online, 1, StationKind.Pc)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var st = AccountSettlement.Settle(charge, new PcAccount("m", 59_501), G(1));
        Assert.Equal(0, st.WriteOffToman);
        Assert.Null(st.Alert);
        Assert.Equal(59_500, st.MoneyConsumedToman);
        Assert.Equal(1, st.UpdatedAccount.MoneyBalanceToman);
    }

    [Fact]
    public void rc_208_fixed_code_has_no_negative_settlement_parts()
    {
        var policy = G(1);
        var start = T.At(2026, 6, 2, 22, 12);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(91), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: policy);
        var discount = new SessionDiscount(LevelPercent: 16, FixedToman: 5_000, ValidFrom: T.At(2026, 6, 2, 23, 4));
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 79_292),
            policy,
            discount: discount);
        Assert.Equal(charge.TotalAmountToman, SettlementSum(settled));
        Assert.True(settled.WriteOffToman >= 0);
        Assert.True(settled.MoneyConsumedToman >= 0);
    }
}