using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>R14e billing mutants — unsafe allocator paths the server API must block.</summary>
public sealed class Pr51R14eBillingTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);

    /// <summary>
    /// Source mutation surface: removing server
    /// <c>RejectWalletOnlyBelowUnroundedGap</c> and letting confirm run allocator gap-draw
    /// (<c>PaymentAllocator</c> / Payments.cs gap shortfall branch) must not stand in for an operator
    /// top-up. Collect with tender 49,000 alone is invalid; server pin r14e_t10 keeps that off the API.
    /// </summary>
    [Fact]
    public void gate_mutant_r14e_t10_source_collect_wallet49000_alone_invalid_payable_50000()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 49_500)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            T.At(2026, 6, 10, 15, 0));
        var rounding = new PaymentRoundingSettings
        {
            ApplyToWallet = true,
            WalletMinBillToman = 5_000,
            AllowWalletShortBalanceDebt = true,
        };
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_000, "m")],
            [new MemberWallet("m", 60_000, 0)],
            DebtPolicy.Limited(50_000),
            walletRoundsLikeCash: true,
            roundingSettings: rounding);
        Assert.False(alloc.IsValid);
    }

    /// <summary>
    /// Pin-killer: if this passes while server T10 passes, someone removed the reject gate but left gap draw on.
    /// Mutation = Payments.cs gap shortfall wallet draw with typed tender 49,000 (regression).
    /// </summary>
    [Fact]
    public void gate_mutant_r14e_t10_killer_wallet49000_alone_must_not_become_valid_collect()
    {
        gate_mutant_r14e_t10_source_collect_wallet49000_alone_invalid_payable_50000();
    }
}