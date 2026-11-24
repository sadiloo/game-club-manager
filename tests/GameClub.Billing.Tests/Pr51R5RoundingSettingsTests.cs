using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class Pr51R5RoundingSettingsTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);
    private static readonly PaymentRoundingSettings Defaults = PaymentRoundingSettings.Default;

    private static InvoiceRequest BuffetRequest(long netToman)
        => new(
            [],
            [new BuffetLine("custom", "خدمت", 1, netToman)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            T.At(2026, 6, 3, 15, 0));

    [Fact]
    public void defaults_reproduce_91667_full_wallet_95000()
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
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 91_667, "m")],
            [new MemberWallet("m", 200_000, 0)],
            roundingSettings: Defaults);
        Assert.True(alloc.IsValid);
        Assert.Equal(95_000, alloc.PayableToman);
    }

    [Fact]
    public void enabled_false_cash_and_wallet_exact_buffet_49500()
    {
        var request = BuffetRequest(49_500);
        var off = Defaults with { Enabled = false };
        var cashOnly = InvoiceCalculator.Calculate(request with { Rounding = RoundingPolicy.Off });
        Assert.Equal(49_500, cashOnly.PayableToman);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_500, "m")],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: off);
        Assert.True(alloc.IsValid);
        Assert.Equal(49_500, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
    }

    [Fact]
    public void apply_to_wallet_false_wallet_exact_cash_rounds()
    {
        var request = BuffetRequest(49_500);
        var settings = Defaults with { ApplyToWallet = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_500, "m")],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(49_500, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        var cashOnly = InvoiceCalculator.Calculate(request);
        Assert.Equal(50_000, cashOnly.PayableToman);
    }

    [Theory]
    [InlineData(9_999L, 9_999L)]
    [InlineData(10_001L, 15_000L)]
    public void wallet_min_bill_10000_threshold(long net, long expectedWalletPayable)
    {
        var request = BuffetRequest(net);
        var settings = Defaults with { WalletMinBillToman = 10_000 };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, net, "m")],
            [new MemberWallet("m", 200_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(expectedWalletPayable, alloc.PayableToman);
    }

    [Fact]
    public void step_1000_rounds_cash_and_wallet()
    {
        var step1k = RoundingPolicy.Create(1_000, RoundingDirection.Up);
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 49_500)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            step1k,
            T.At(2026, 6, 3, 15, 0));
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 50_000, "m")],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: Defaults);
        Assert.True(alloc.IsValid);
        Assert.Equal(50_000, alloc.PayableToman);
        Assert.Equal(500, alloc.RoundingToman);
    }

    [Fact]
    public void allow_debt_false_still_lifts_wallet_to_rounded_collectible()
    {
        var request = BuffetRequest(49_500);
        var settings = Defaults with { AllowWalletShortBalanceDebt = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_500, "m")],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(50_000, alloc.PayableToman);
    }

    [Fact]
    public void allow_debt_false_rejects_wallet_debt_remainder_for_rounding_gap()
    {
        var request = BuffetRequest(49_500);
        var settings = Defaults with { AllowWalletShortBalanceDebt = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 50_000, "m")],
            [new MemberWallet("m", 49_700, 0)],
            DebtPolicy.Limited(50_000),
            roundingSettings: settings);
        Assert.False(alloc.IsValid);
    }

    [Fact]
    public void f05_gap_draw_respects_wallet_tender_cap_s9_wallet_line_49600()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(49_500),
            [new Tender(PaymentMethod.Wallet, 49_600, "m"), new Tender(PaymentMethod.Cash, 400)],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: Defaults);
        Assert.True(alloc.IsValid);
        Assert.Equal(49_600, alloc.Tenders.Single(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(50_000, alloc.PayableToman);
    }

    [Fact]
    public void f05_scenario_s2_mixed_wallet_tender_equals_offer_not_lifted()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(49_500),
            [new Tender(PaymentMethod.Wallet, 49_500, "m"), new Tender(PaymentMethod.Cash, 500)],
            [new MemberWallet("m", 60_000, 0)],
            roundingSettings: Defaults);
        Assert.True(alloc.IsValid);
        Assert.Equal(50_000, alloc.PayableToman);
        Assert.Equal(49_500, alloc.Tenders.Single(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(500, alloc.Tenders.Single(t => t.Method == PaymentMethod.Cash).AmountToman);
    }

    [Fact]
    public void allow_debt_false_cash_remainder_ok_for_rounding_gap()
    {
        var request = BuffetRequest(49_500);
        var settings = Defaults with { AllowWalletShortBalanceDebt = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_500, "m"), new Tender(PaymentMethod.Cash, 500)],
            [new MemberWallet("m", 49_700, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(50_000, alloc.PayableToman);
    }

    [Fact]
    public void r14_d2_mixed_5000_wallet_1000_cash_5000_settles_exact_wallet_min_10k()
    {
        var request = BuffetRequest(5_000);
        var settings = Defaults with { WalletMinBillToman = 10_000 };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 1_000, "m"), new Tender(PaymentMethod.Cash, 4_000)],
            [new MemberWallet("m", 10_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(5_000, alloc.PayableToman);
    }

    [Fact]
    public void r14_d2_mixed_8000_settles_exact_not_above_all_cash_wallet_min_10k()
    {
        var request = BuffetRequest(8_000);
        var settings = Defaults with { WalletMinBillToman = 10_000 };
        var cashRounded = InvoiceCalculator.Calculate(request);
        Assert.Equal(10_000, cashRounded.PayableToman);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 2_000, "m"), new Tender(PaymentMethod.Cash, 6_000)],
            [new MemberWallet("m", 10_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(8_000, alloc.PayableToman);
        Assert.True(alloc.PayableToman <= cashRounded.PayableToman);
    }

    [Fact]
    public void r14_sub_5000_499_wallet_498_cash_1_stays_exact()
    {
        var request = BuffetRequest(499);
        var settings = Defaults with { WalletMinBillToman = 10_000 };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 498, "m"), new Tender(PaymentMethod.Cash, 1)],
            [new MemberWallet("m", 10_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid);
        Assert.Equal(499, alloc.PayableToman);
    }
}