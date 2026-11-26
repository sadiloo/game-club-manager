using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Mutants killed by R14o owner ruling (write-off + ApplyToWallet ON stays 15٬500).</summary>
public sealed class Pr51R14oMutantBehaviorTests
{
    private static InvoiceRequest BuffetRequest(long net) =>
        new(
            [],
            [new BuffetLine("custom", "بوفه", 1, net)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            new RoundingPolicy(true, 5_000, RoundingDirection.Up, RoundingScope.WholeInvoice),
            T.At(2026, 6, 10, 15, 0));

    [Fact]
    public void gate_mutant_r14o_writeoff_cash_15500_rounds_collectible_to_20000()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = true };
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Cash, 15_500)],
            walletRoundsLikeCash: true,
            roundingSettings: settings);
        Assert.False(alloc.IsValid);
        Assert.Equal(20_000, alloc.PayableToman);
        Assert.NotEqual(15_500, alloc.PayableToman);
    }
}