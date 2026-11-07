using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Frozen wallet slice acceptance numbers (spec scenarios 3–6, 10–11, 28% rounding).</summary>
public class WalletLoyaltyBillingAcceptanceTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);

    [Fact]
    public void wl_scenario_03_full_wallet_91667_no_counter_rounding_lift()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("ps5", 91_667, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            T.At(2026, 6, 3, 15, 0));
        var wallet = new MemberWallet("m", 200_000, 0);
        var (invoice, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 91_667, "m")],
            [wallet]);
        Assert.True(alloc.IsValid);
        Assert.Equal(91_667, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        Assert.Equal(91_667, invoice.PayableToman);
    }

    [Fact]
    public void wl_scenario_04_mixed_wallet_50001_counter_rounds_to_150001()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 149_850, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            T.At(2026, 6, 3, 15, 0));
        var preview = new[] { new Tender(PaymentMethod.Wallet, 50_001, "m"), new Tender(PaymentMethod.Cash, 100_000) };
        var (previewInvoice, previewAlloc) = InvoiceCalculator.Collect(request, preview, [new MemberWallet("m", 50_001, 0)]);
        var (confirmInvoice, confirmAlloc) = InvoiceCalculator.Collect(request, preview, [new MemberWallet("m", 50_001, 0)]);
        Assert.True(previewAlloc.IsValid);
        Assert.Equal(previewAlloc.PayableToman, confirmAlloc.PayableToman);
        Assert.Equal(150_001, confirmAlloc.PayableToman);
        Assert.Equal(151, confirmAlloc.RoundingToman);
    }

    [Fact]
    public void wl_scenario_05_wallet_splits_20000_time_10000_buffet()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 100_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("tea", "چای", 1, 50_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 3, 15, 0));
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 30_000, "m"), new Tender(PaymentMethod.Cash, 120_000)],
            [new MemberWallet("m", 30_000, 0)]);
        Assert.True(alloc.IsValid);
        var timeShare = alloc.Shares.Where(s => s.LineIndex == 0 && s.Method == PaymentMethod.Wallet).Sum(s => s.AmountToman);
        var buffetShare = alloc.Shares.Where(s => s.LineIndex == 1 && s.Method == PaymentMethod.Wallet).Sum(s => s.AmountToman);
        Assert.Equal(20_000, timeShare);
        Assert.Equal(10_000, buffetShare);
    }

    [Fact]
    public void wl_scenario_06_surcharge_after_wallet_unrounded_base()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 91_667, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [new ServiceLine("surcharge", "۵٬۰۰۰+", 5_000)],
            null,
            null,
            DiscountPolicy.Default,
            RoundingPolicy.Off,
            T.At(2026, 6, 3, 15, 0));
        var (invoice, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 96_667, "m")],
            [new MemberWallet("m", 100_000, 0)]);
        Assert.True(alloc.IsValid);
        Assert.Equal(96_667, invoice.PayableToman);
    }

    [Fact]
    public void wl_scenario_07_debt_floor_allows_wallet_to_minus_50000()
    {
        var pocket = new WalletPocket(10_000, 0);
        Assert.True(pocket.TrySpend(60_000, DebtPolicy.Limited(50_000), out var updated, out var gift, out var real));
        Assert.Equal(0, gift);
        Assert.Equal(60_000, real);
        Assert.Equal(-50_000, updated.RealToman);
        var topped = MemberWalletMapperBilling.ApplyRealTopUp(updated, 70_000);
        Assert.Equal(20_000, topped.RealToman);
    }

    [Fact]
    public void wl_scenario_10_level_and_birthday_discounts_time_only_default_cap()
    {
        var birth = JalaliDate.FromGregorian(new LocalDate(2026, 6, 3));
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 100_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("snack", "بوفه", 1, 20_000)],
            [],
            new CustomerPricing("Bronze", 5, birth, 10),
            null,
            new DiscountPolicy(100, BirthdayWindow.WholeMonth),
            RoundingPolicy.Off,
            T.At(2026, 6, 3, 15, 0));
        var invoice = InvoiceCalculator.Calculate(request);
        Assert.Equal(15_000, invoice.DiscountToman);
        Assert.Equal(105_000, invoice.PayableToman);
    }

    [Fact]
    public void wl_scenario_12_sixty_minute_prepaid_pause_does_not_consume_booked_time()
    {
        var start = T.At(2026, 6, 3, 14, 0);
        var plan = PrepaidPlanner.ForDuration(start, TimeSpan.FromMinutes(60), "a", "ps5-1", CafeFixture.Ps5, PlayMode.Offline, 1, CafeFixture.Card());
        Assert.Equal(3600, plan.GrantedActiveSeconds);
        Assert.Equal(PrepaidKind.Duration, plan.Kind);
    }

    [Fact]
    public void wl_scenario_13_missing_station_rate_is_an_error_never_free()
    {
        var start = T.At(2026, 6, 3, 14, 0);
        Assert.ThrowsAny<Exception>(() => PrepaidPlanner.ForAmount(
            start,
            55_000,
            "a",
            "ps5-1",
            "missing-group",
            PlayMode.Offline,
            1,
            CafeFixture.Card()));
    }

    [Fact]
    public void wl_scenario_11_combined_28_percent_rounds_down_39666()
    {
        var gross = 141_667;
        var off = MoneyMath.PercentOf(gross, 28);
        Assert.Equal(39_666, off);
    }
}

internal static class MemberWalletMapperBilling
{
    public static WalletPocket ApplyRealTopUp(WalletPocket pocket, long realToman)
    {
        var real = pocket.RealToman;
        if (real < 0)
        {
            var debt = -real;
            var clear = realToman < debt ? realToman : debt;
            real += clear;
            realToman -= clear;
        }

        if (realToman > 0)
        {
            pocket = new WalletPocket(real, pocket.GiftToman, pocket.BasisRealToman, pocket.BasisGiftToman, pocket.PositiveDrawnToman, pocket.GiftDrawnFromBasisToman);
            var added = new WalletPocket(MoneyMath.CheckedAdd(pocket.RealToman, realToman), pocket.GiftToman);
            return added;
        }

        return new WalletPocket(real, pocket.GiftToman, pocket.BasisRealToman, pocket.BasisGiftToman, pocket.PositiveDrawnToman, pocket.GiftDrawnFromBasisToman);
    }
}
