using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round9CapTests
{
    static BillingPolicy G(int g) => new(g, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    static RateCard Flat() => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    static long Parts(Settlement st)
        => st.PackageCoveredToman + st.MoneyConsumedToman + st.DiscountToman
            + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

    static void AssertConservation(SessionCharge charge, Settlement st)
    {
        Assert.Equal(charge.TotalAmountToman, Parts(st));
        Assert.Equal(st.MoneyConsumedToman, st.MoneyConsumedToman);
    }

    static void AssertInvoice(PcPlayResult r)
    {
        Assert.NotNull(r.ChargeInvoice);
        Assert.Equal(r.Settlement.MoneyConsumedToman, r.ChargeInvoice.PayableToman);
        Assert.Equal(r.ChargeInvoice.PayableToman, r.ChargeInvoice.Lines.Sum(l => l.AmountToman));
        var invDisc = -r.ChargeInvoice.Lines.Where(l => l.Kind == InvoiceLineKind.Discount).Sum(l => l.AmountToman);
        Assert.Equal(r.Settlement.DiscountToman, invDisc);
    }

    [Fact]
    public void r9_1_fixed_code_mid_session_balance_300_pins_debit_discount_grace_and_invoice()
    {
        var d0 = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", d0, d0.AddMinutes(2), "st", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var code = new DiscountCode("C41", 41, 11_485, d0.AddSeconds(36), null, null, null);
        var account = new CustomerAccount(
            "m",
            WalletSettings.Shared with { ApplyDiscountsToPc = true },
            new WalletPocket(300, 0),
            new WalletPocket(0, 0),
            []);
        foreach (var ord in new[] { ConsumptionOrder.PackagesThenMoney, ConsumptionOrder.MoneyThenPackages })
        {
            var r = CustomerPlay.Settle(
                charge,
                account,
                policy: new AccountPolicy(ord),
                billing: G(1),
                pricing: new CustomerPricing("Titan", 28, null, 0),
                code: code,
                issuedAt: d0.AddMinutes(2));
            var st = r.Settlement;
            AssertConservation(charge, st);
            Assert.Equal(300, st.MoneyConsumedToman);
            Assert.Equal(1_707, st.DiscountToman);
            Assert.Equal(660, st.Grace!.WaivedToman);
            Assert.Equal(0, st.WriteOffToman);
            AssertInvoice(r);
        }
    }

    [Fact]
    public void r9_2_block_grace_money_first_shows_ledger_discount_and_full_omitted()
    {
        var d0 = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", d0, d0.AddHours(1).AddSeconds(31), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: G(15));
        var r = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(4_458, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 61, 200_000)]),
            policy: new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            billing: G(15),
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: new DiscountCode("C", 35, 6_268, d0.AddSeconds(22 * 60 + 19), null, null, null),
            issuedAt: d0.AddHours(1).AddSeconds(31));
        var st = r.Settlement;
        AssertConservation(charge, st);
        Assert.Equal(3_960, st.MoneyConsumedToman);
        Assert.Equal(3_374, st.DiscountToman);
        Assert.Equal(18_333, st.OmittedToman);
        Assert.Equal(0, st.WriteOffToman);
        Assert.Null(st.Grace);
        AssertInvoice(r);
    }

    [Theory]
    [InlineData(ConsumptionOrder.MoneyThenPackages)]
    [InlineData(ConsumptionOrder.PackagesThenMoney)]
    public void rc_213_vip_2355_balance_348_pins_parts_in_both_orders(ConsumptionOrder ord)
    {
        var s = T.At(2026, 6, 2, 23, 25);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", s, s.AddMinutes(34), "st", PilotSeed.PcVipGroupId, PlayMode.Offline, 1)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        Assert.Equal(62_333, charge.TotalAmountToman);
        var r = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(348, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 28, 200_000)]),
            policy: new AccountPolicy(ord),
            billing: G(1),
            pricing: new CustomerPricing("L", 30, null, 0),
            code: DiscountCode.FixedOff("F", 9_000, validFrom: T.At(2026, 6, 2, 23, 55)),
            issuedAt: s.AddMinutes(34));
        var st = r.Settlement;
        AssertConservation(charge, st);
        Assert.Equal(348, st.MoneyConsumedToman);
        Assert.Equal(8_433, st.DiscountToman);
        Assert.Equal(936, st.Grace!.WaivedToman);
        Assert.Equal(1_283, st.WriteOffToman);
        Assert.Equal(0, st.OmittedToman);
        AssertInvoice(r);
    }

    [Theory]
    [InlineData(ConsumptionOrder.MoneyThenPackages)]
    [InlineData(ConsumptionOrder.PackagesThenMoney)]
    public void rc_213_daytime_1000_balance_348_pins_parts_in_both_orders(ConsumptionOrder ord)
    {
        var s = T.At(2026, 6, 2, 10, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", s, s.AddMinutes(34), "st", PilotSeed.PcVipGroupId, PlayMode.Offline, 1)]),
            PilotSeed.CreateCard(),
            policy: G(1));
        var r = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(348, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 28, 200_000)]),
            policy: new AccountPolicy(ord),
            billing: G(1),
            pricing: new CustomerPricing("L", 30, null, 0),
            code: DiscountCode.FixedOff("F", 9_000, validFrom: s.AddMinutes(30)),
            issuedAt: s.AddMinutes(34));
        var st = r.Settlement;
        AssertConservation(charge, st);
        Assert.Equal(348, st.MoneyConsumedToman);
        Assert.Equal(8_433, st.DiscountToman);
        Assert.Equal(936, st.Grace!.WaivedToman);
        Assert.Equal(1_283, st.WriteOffToman);
        Assert.Equal(0, st.OmittedToman);
        AssertInvoice(r);
    }
}
