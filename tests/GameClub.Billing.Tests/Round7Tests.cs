using System.Diagnostics;
using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round7Tests
{
    [Fact]
    public void negative_grace_minute_conserves_gross_and_settle_does_not_throw()
    {
        var flat = Flat();
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
            flat,
            policy: policy);
        var account = new CustomerAccount(
            "m",
            WalletSettings.Shared with { ApplyDiscountsToPc = true, GraceMinuteEnabled = true },
            new WalletPocket(1_000_000, 0),
            new WalletPocket(0, 0),
            [new TimePackage("p", 8, 200_000)]);
        var code = new DiscountCode("FX", 0, 5_000, T.At(2026, 6, 2, 23, 1), null, null, null);
        var result = CustomerPlay.Settle(
            charge,
            account,
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: code,
            policy: new AccountPolicy(ConsumptionOrder.PackagesThenMoney),
            billing: policy,
            issuedAt: start.AddMinutes(63));
        var st = result.Settlement;
        var sum = st.PackageCoveredToman + st.MoneyConsumedToman + st.DiscountToman
            + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;
        Assert.Equal(charge.TotalAmountToman, sum);
        Assert.NotNull(result.ChargeInvoice);
        Assert.Equal(st.MoneyConsumedToman, result.ChargeInvoice!.PayableToman);
    }

    [Theory]
    [InlineData(5, 8)]
    [InlineData(5, 10)]
    [InlineData(15, 8)]
    [InlineData(15, 10)]
    public void large_balance_after_package_minutes_has_no_phantom_grace(int granularity, int packageMinutes)
    {
        var flat = Flat();
        var policy = new BillingPolicy(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(63), "st", "g", PlayMode.Offline, 1)]),
            flat,
            policy: policy);
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 1_000_000, [new TimePackage("p", packageMinutes, 200_000)]),
            billing: policy);
        Assert.Null(settled.Grace);
        Assert.Equal(0, settled.OmittedToman);
        var sum = settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.DiscountToman
            + settled.WriteOffToman + settled.UncoveredToman + settled.OmittedToman;
        Assert.Equal(charge.TotalAmountToman, sum);
    }

    [Fact]
    public void station_directory_lookup_ignores_case_and_surrounding_spaces()
    {
        var stations = new StationDirectory(
            [new StationGroup("g", "PC", StationKind.Pc)],
            [new Station("pc-1", "PC 1", "g")]);
        var start = T.At(2026, 6, 2, 10, 0);
        var session = new Session("ok", [
            new PlaySegment("a", start, start.AddMinutes(10), " pc-1 ", "g", PlayMode.Offline, 1),
        ]);
        var charge = SessionPricer.Price(session, Flat(), stations: stations);
        Assert.True(charge.TotalBilledMinutes > 0);
    }

    [Fact]
    public void money_then_packages_uses_each_minutes_half_up_price_for_code_gross()
    {
        var flat = new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = T.At(2026, 6, 4, 12, 0);
        var end = start.AddHours(2);
        var charge = SessionPricer.Price(
            new Session("s", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
            flat,
            policy: policy);
        var spec = new SessionDiscount(
            LevelPercent: 28,
            CodePercent: 15,
            FixedToman: 5_000,
            ValidFrom: start.AddMinutes(30));
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 500_000, []),
            new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            policy,
            discount: spec);
        var expected = Round6Tests.IndependentOracleDiscountForCharge(charge, spec);
        Assert.Equal(expected, settled.DiscountToman);
    }

    [Fact]
    public void block_billing_net_of_keeps_negative_step_when_fixed_exceeds_minute_net()
    {
        var start = T.At(2026, 6, 2, 22, 0);
        var flat = Flat();
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(51), "st", "g", PlayMode.Offline, 1)]),
            flat,
            policy: policy);
        var spec = new SessionDiscount(LevelPercent: 28, FixedToman: 5_000, ValidFrom: T.At(2026, 6, 2, 22, 48));
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 51_816, [new TimePackage("p", 8, 200_000)]),
            billing: policy,
            discount: spec);
        var parts = settled.PackageCoveredToman + settled.MoneyConsumedToman + settled.DiscountToman
            + (settled.Grace?.WaivedToman ?? 0) + settled.WriteOffToman + settled.OmittedToman + settled.UncoveredToman;
        Assert.Equal(charge.TotalAmountToman, parts);
        Assert.Equal(51_773, settled.MoneyConsumedToman);
        Assert.Equal(7_321, settled.OmittedToman);
    }

    [Fact]
    public void one_year_settle_stays_under_budget()
    {
        var flat = Flat();
        var policy = BillingPolicy.Default;
        var start = T.At(2025, 6, 2, 10, 0);
        var end = start.AddYears(1);
        var charge = SessionPricer.Price(
            new Session("y", [new PlaySegment("a", start, end, "st", "g", PlayMode.Offline, 1)]),
            flat,
            policy: policy);
        var sw = Stopwatch.StartNew();
        _ = AccountSettlement.Settle(charge, new PcAccount("m", 10_000_000_000, []), billing: policy);
        sw.Stop();
        var budgetMs = Math.Max(100, sw.ElapsedMilliseconds * 10);
        Assert.True(sw.ElapsedMilliseconds < budgetMs, $"one-year settle took {sw.ElapsedMilliseconds} ms (budget {budgetMs} ms)");
    }

    private static RateCard Flat() => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();
}