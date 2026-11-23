namespace GameClub.Billing;

/// <summary>Branch rounding behaviour at payment time (defaults match pre-settings rulings).</summary>
public sealed record PaymentRoundingSettings
{
    public bool Enabled { get; init; } = true;

    public bool ApplyToWallet { get; init; } = true;

    public long WalletMinBillToman { get; init; } = WalletPaymentRounding.CashLikeThresholdToman;

    public bool AllowWalletShortBalanceDebt { get; init; } = true;

    public static PaymentRoundingSettings Default { get; } = new();
}

/// <summary>Single entry point for cash and wallet rounding decisions.</summary>
public static class PaymentRounding
{
    public sealed record Resolution(
        RoundingPolicy BranchRounding,
        RoundingPolicy EffectiveRounding,
        bool WalletRoundsLikeCash);

    public static Resolution Resolve(
        long unroundedPayableToman,
        RoundingPolicy branchRounding,
        PaymentRoundingSettings settings,
        bool walletRoundsLikeCashFlag)
    {
        if (!settings.Enabled || !branchRounding.Enabled)
        {
            return new(RoundingPolicy.Off, RoundingPolicy.Off, false);
        }

        var subThreshold = unroundedPayableToman < settings.WalletMinBillToman;
        var effectiveRounding = subThreshold ? RoundingPolicy.Off : branchRounding;
        var walletLikeCash = walletRoundsLikeCashFlag && settings.ApplyToWallet && !subThreshold;
        return new(branchRounding, effectiveRounding, walletLikeCash);
    }
}