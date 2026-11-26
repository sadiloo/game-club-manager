using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>
/// R14o ruling A (write-off shape, ApplyToWallet ON, W500+cash, bill 15٬500): server E2E pins the API;
/// these Collect pins mirror <c>walletRoundsLikeCash: false</c> (pc write-off) vs mutant whole-bill round.
/// </summary>
public sealed class Pr51R14oMutantBehaviorTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);

    private static InvoiceRequest WriteOffBuffetInvoice(long net) =>
        new(
            [],
            [new BuffetLine("custom", "بوفه", 1, net)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            T.At(2026, 6, 10, 15, 0));

    private static readonly PaymentRoundingSettings ApplyWalletOn = PaymentRoundingSettings.Default with { ApplyToWallet = true };

    private static readonly Tender[] OwnerMixedTenders =
    [
        new Tender(PaymentMethod.Wallet, 500, "m"),
        new Tender(PaymentMethod.Cash, 15_000),
    ];

    private static readonly MemberWallet[] OwnerWallet = [new MemberWallet("m", 10_000, 0)];

    [Fact]
    public void r14o_owner_writeoff_shape_apply_wallet_on_mixed_15500_collect_payable_15500()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            WriteOffBuffetInvoice(15_500),
            OwnerMixedTenders,
            OwnerWallet,
            walletRoundsLikeCash: false,
            roundingSettings: ApplyWalletOn);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_500, alloc.PayableToman);
        Assert.NotEqual(20_000, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
    }

    private static readonly Tender[] OwnerUnderTenderMixed =
    [
        new Tender(PaymentMethod.Wallet, 500, "m"),
        new Tender(PaymentMethod.Cash, 14_500),
    ];

    /// <summary>Mutation A2 (whole-bill round on write-off): payable becomes 20٬000; under-tender vs 15٬500 pin must fail.</summary>
    [Fact]
    public void gate_mutant_r14o_whole_bill_round_writeoff_mixed_payable_20000()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            WriteOffBuffetInvoice(15_500),
            OwnerUnderTenderMixed,
            OwnerWallet,
            walletRoundsLikeCash: false,
            roundingSettings: ApplyWalletOn);
        Assert.False(alloc.IsValid);
        Assert.Equal(15_500, alloc.PayableToman);
        Assert.NotEqual(20_000, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        Assert.DoesNotContain(alloc.Errors, e => e.Message.Contains("20000", StringComparison.Ordinal));
    }
}