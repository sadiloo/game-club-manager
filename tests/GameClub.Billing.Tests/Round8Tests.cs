using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Round8Tests
{
    const string V = PilotSeed.PcVipGroupId;

    static BillingPolicy G(int granularity)
        => new(granularity, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);

    static RateCard Flat()
        => new RateCardBuilder().AddBase("g", PlayMode.Offline, 1, 110_000).Build();

    static SessionCharge OneHourFlat(DateTimeOffset start, int minutes, BillingPolicy policy)
        => SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddMinutes(minutes), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: policy);

    static (Settlement st, SessionCharge charge) MoneyFirstVipBand(
        long balance,
        SessionDiscount? discount = null,
        int granularity = 1,
        long packageMinutes = 30)
    {
        var start = T.At(2026, 6, 2, 23, 50);
        var end = T.At(2026, 6, 3, 0, 20);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, end, "st", V, PlayMode.Offline, 1, StationKind.Pc)]),
            PilotSeed.CreateCard(),
            policy: G(granularity));
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", balance, [new TimePackage("p", packageMinutes, 200_000)]),
            new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            G(granularity),
            discount: discount);
        return (settled, charge);
    }

    static long SettlementSum(Settlement st)
        => st.PackageCoveredToman + st.MoneyConsumedToman + st.DiscountToman
            + (st.Grace?.WaivedToman ?? 0) + st.WriteOffToman + st.UncoveredToman + st.OmittedToman;

    [Fact]
    public void rc_197_money_first_keeps_fixed_code_discount()
    {
        var policy = G(5);
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = OneHourFlat(start, 63, policy);
        var code = DiscountCode.FixedOff("FX", 5_000, validFrom: T.At(2026, 6, 2, 23, 1));
        var result = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(70_000, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 8, 200_000)]),
            policy: new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            billing: policy,
            pricing: new CustomerPricing("Titan", 28, null, 0),
            code: code,
            issuedAt: start.AddMinutes(63));
        var st = result.Settlement;
        Assert.Equal(charge.TotalAmountToman, SettlementSum(st));
        Assert.Equal(34_260, st.DiscountToman);
        Assert.Equal(70_000, st.MoneyConsumedToman);
        Assert.Equal(8, st.PackageMinutesConsumed);
        Assert.Null(st.Grace);
    }

    [Fact]
    public void rc_202_money_first_band_edge_birthday_uses_discounted_marginals()
    {
        var birthday = new SessionDiscount(
            BirthdayPercent: 20,
            BirthDate: JalaliDate.FromGregorian(new LocalDate(2026, 6, 3)));
        var (st, _) = MoneyFirstVipBand(27_000, birthday);
        Assert.Equal(27_000, st.MoneyConsumedToman);
        Assert.Equal(15, st.PackageMinutesConsumed);
        Assert.Null(st.Grace);
    }

    [Fact]
    public void rc_202_money_first_band_edge_code_from_midnight_uses_discounted_marginals()
    {
        var code = new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 3, 0, 0));
        var (st, _) = MoneyFirstVipBand(23_750, code);
        Assert.Equal(23_750, st.MoneyConsumedToman);
        Assert.Equal(15, st.PackageMinutesConsumed);
        Assert.Null(st.Grace);
    }

    [Fact]
    public void rc_206_money_first_tail_after_package_uses_real_minute()
    {
        var start = T.At(2026, 6, 2, 22, 0);
        var charge = SessionPricer.Price(
            new Session("x", [new PlaySegment("a", start, start.AddHours(1), "st", "g", PlayMode.Offline, 1)]),
            Flat(),
            policy: G(1));
        var st = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 56_000, [new TimePackage("p", 10, 200_000)]),
            new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            G(1),
            discount: new SessionDiscount(CodePercent: 50, ValidFrom: T.At(2026, 6, 2, 22, 40)));
        Assert.Equal(charge.TotalAmountToman, SettlementSum(st));
        Assert.Equal(T.At(2026, 6, 2, 22, 41), st.Grace?.At);
        // B2: full discounted net of grace minute — gross marginal 1833 at min 42, 50% → 1833-916=917.
        Assert.Equal(917, st.Grace!.WaivedToman);
    }

    [Fact]
    public void rc_207_money_first_one_toman_short_does_not_buy_next_minute_or_write_off()
    {
        var (st, _) = MoneyFirstVipBand(20_499);
        Assert.Equal(0, st.WriteOffToman);
        Assert.Null(st.Alert);
        Assert.Equal(20, st.PackageMinutesConsumed);
        Assert.Equal(18_333, st.MoneyConsumedToman);
        Assert.Equal(2_166, st.UpdatedAccount.MoneyBalanceToman);
    }

    [Fact]
    public void rc_208_money_first_fixed_code_has_no_negative_settlement_parts()
    {
        var policy = G(1);
        var start = T.At(2026, 6, 2, 22, 12);
        var charge = OneHourFlat(start, 91, policy);
        var discount = new SessionDiscount(LevelPercent: 16, FixedToman: 5_000, ValidFrom: T.At(2026, 6, 2, 23, 4));
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount("m", 79_292, [new TimePackage("p", 38, 200_000)]),
            new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            policy,
            discount: discount);
        Assert.Equal(charge.TotalAmountToman, SettlementSum(settled));
        Assert.True(settled.WriteOffToman >= 0);
        Assert.True(settled.PackageCoveredToman >= 0);
        Assert.True(settled.MoneyConsumedToman >= 0);

        var play = CustomerPlay.Settle(
            charge,
            new CustomerAccount(
                "m",
                WalletSettings.Shared with { ApplyDiscountsToPc = true },
                new WalletPocket(79_292, 0),
                new WalletPocket(0, 0),
                [new TimePackage("p", 38, 200_000)]),
            policy: new AccountPolicy(ConsumptionOrder.MoneyThenPackages),
            billing: policy,
            pricing: new CustomerPricing("L", 16, null, 0),
            code: DiscountCode.FixedOff("F", 5_000, validFrom: T.At(2026, 6, 2, 23, 4)),
            issuedAt: start.AddMinutes(91));
        Assert.Equal(charge.TotalAmountToman, SettlementSum(play.Settlement));
        Assert.Equal(play.Settlement.MoneyConsumedToman, play.ChargeInvoice!.PayableToman);
    }

    [Fact]
    public void station_directory_rejects_duplicate_after_normalizing_whitespace()
    {
        var group = new StationGroup("pcg", "PC", StationKind.Pc);
        var ex = Assert.Throws<BillingException>(() =>
            new StationDirectory(
                [group],
                [new Station("pc1", "a", "pcg"), new Station(" pc1 ", "b", "pcg")]));
        Assert.Equal(BillingErrors.InvalidTariff, ex.Code);

        var dir = new StationDirectory([group], [new Station(" pc9 ", "padded", "pcg")]);
        Assert.True(dir.TryGetGroup("pc9", out _));
        Assert.True(dir.TryGetGroup(" pc9 ", out _));
    }
}
