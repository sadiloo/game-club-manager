using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Behavioral pins for R14f mutants — each case must fail if the named source surface is removed or inverted.</summary>
public sealed class Pr51R14fMutantBehaviorTests
{
  /// <summary>Mutant: PaymentSplitter re-check when tendered==payable removed (ML2 exact wallet+cash).</summary>
    [Fact]
    public void mutant_surface_ml2_splitter_exact_wallet_cash_must_stay_valid()
    {
        var id = "m-r14f";
        var split = PaymentSplitter.Split(
            50_000,
            [new Tender(PaymentMethod.Wallet, 49_500, id), new Tender(PaymentMethod.Cash, 500)],
            [new MemberWallet(id, 49_700, 0)]);
        Assert.True(split.IsValid);
        Assert.Empty(split.Errors);
    }

    /// <summary>Mutant: gap-draw wallet-only below unrounded Collect must stay invalid (WOb14 / T10).</summary>
    [Fact]
    public void mutant_surface_wallet_only_below_unrounded_collect_invalid()
    {
        var step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 49_500)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            step5Up,
            T.At(2026, 6, 10, 15, 0));
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 49_000, "m")],
            [new MemberWallet("m", 60_000, 0)],
            DebtPolicy.Limited(50_000),
            walletRoundsLikeCash: true,
            roundingSettings: new PaymentRoundingSettings
            {
                ApplyToWallet = true,
                WalletMinBillToman = 5_000,
                AllowWalletShortBalanceDebt = true,
            });
        Assert.False(alloc.IsValid);
    }
}