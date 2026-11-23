using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Owner rulings R4c Q2/Q3 (2026-11-24): mixed wallet+cash uses one bill-level total; cash remainder is never rounded alone.</summary>
public sealed class WalletLoyaltyR4cQ2Q3BillingTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);
    private static readonly DateTimeOffset Issued = T.At(2026, 6, 3, 15, 0);

    private static void AssertQ2Collect(InvoiceRequest request)
    {
        const long net = 3_000;
        const long wallet = 1_000;
        const long cash = 2_000;
        var tenders = new[]
        {
            new Tender(PaymentMethod.Wallet, wallet, "m"),
            new Tender(PaymentMethod.Cash, cash),
        };
        var (invoice, alloc) = InvoiceCalculator.Collect(request, tenders, [new MemberWallet("m", wallet, 0)]);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(net, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        Assert.Equal(net, invoice.PayableToman);
        Assert.Equal(wallet, alloc.Tenders.First(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(cash, alloc.Tenders.First(t => t.Method == PaymentMethod.Cash).AmountToman);
    }

    private static void AssertQ3Collect(InvoiceRequest request)
    {
        const long wallet = 50_001;
        const long cash = 99_999;
        const long collectible = 150_000;
        Assert.Equal(collectible, InvoiceCalculator.Calculate(request).PayableToman);
        var tenders = new[]
        {
            new Tender(PaymentMethod.Wallet, wallet, "m"),
            new Tender(PaymentMethod.Cash, cash),
        };
        var (invoice, alloc) = InvoiceCalculator.Collect(request, tenders, [new MemberWallet("m", wallet, 0)]);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(collectible, alloc.PayableToman);
        Assert.Equal(150, alloc.RoundingToman);
        Assert.Equal(collectible, invoice.PayableToman);
        Assert.Equal(wallet, alloc.Tenders.First(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(cash, alloc.Tenders.First(t => t.Method == PaymentMethod.Cash).AmountToman);
    }

    [Fact]
    public void r4c_q2_mixed_session_time_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 3_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_buffet_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 3_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_game_service_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [],
            [],
            [new ServiceLine("game", "بازی", 3_000)],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_time_buffet_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 2_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("tea", "چای", 1, 1_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_session_time_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 149_850, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_buffet_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 149_850)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_game_service_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [],
            [],
            [new ServiceLine("game", "بازی", 149_850)],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_time_buffet_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 140_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("tea", "چای", 1, 9_850)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }
}