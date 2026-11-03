namespace GameClub.Billing;

public enum WalletMode
{
    /// <summary>One balance pays PC sessions and console or table invoices.</summary>
    Shared = 0,

    /// <summary>A PC balance that never goes negative, and a separate non-PC wallet that may.</summary>
    Separate = 1,
}

public enum TopUpTarget
{
    /// <summary>The single wallet. Valid only in <see cref="WalletMode.Shared"/>.</summary>
    Shared = 0,

    /// <summary>The PC balance. Valid only in <see cref="WalletMode.Separate"/>.</summary>
    Pc = 1,

    /// <summary>The console and table wallet. Valid only in <see cref="WalletMode.Separate"/>.</summary>
    NonPc = 2,
}

/// <summary>
/// Branch setting for how a customer's money is held.
/// <see cref="AllowPcWhileInDebt"/> matters only in <see cref="WalletMode.Shared"/>:
/// false (the default) blocks PC play while the shared balance is negative;
/// true lets PC use package minutes, and PC still cannot push the balance further negative.
/// <see cref="Debt"/> is how far a console or table charge may go. PC play never uses it to go further into debt.
/// </summary>
public sealed record WalletSettings(WalletMode Mode, bool AllowPcWhileInDebt, DebtPolicy Debt, bool GraceMinuteEnabled = true)
{
    public WalletSettings(WalletMode mode, bool allowPcWhileInDebt = false)
        : this(mode, allowPcWhileInDebt, DebtPolicy.Unlimited, true)
    {
        EnsureValid();
    }

    public static WalletSettings Shared { get; } = new(WalletMode.Shared);

    public static WalletSettings Separate { get; } = new(WalletMode.Separate);

    public WalletSettings WithDebt(DebtPolicy debt) => this with { Debt = debt };

    public void EnsureValid()
    {
        if (Mode == WalletMode.Separate && AllowPcWhileInDebt)
        {
            throw new BillingException(
                "invalid_policy",
                "AllowPcWhileInDebt applies only when PC and console share one wallet.");
        }
    }
}

/// <summary>
/// One customer's packages and money. In <see cref="WalletMode.Shared"/>, <see cref="Pc"/> is the only
/// wallet and <see cref="NonPc"/> stays zero. In <see cref="WalletMode.Separate"/>, <see cref="Pc"/> never
/// goes negative and <see cref="NonPc"/> is what console and table invoices charge.
/// </summary>
public sealed record CustomerAccount(
    string Id,
    WalletSettings Settings,
    WalletPocket Pc,
    WalletPocket NonPc,
    IReadOnlyList<TimePackage> Packages)
{
    public long PcMoneyToman => Pc.TotalToman;

    public long NonPcMoneyToman => NonPc.TotalToman;

    public CustomerAccount TopUp(TopUpTarget target, long realToman, long giftToman = 0)
    {
        Settings.EnsureValid();
        if (realToman < 0 || giftToman < 0)
        {
            throw new BillingException("invalid_topup", "A top-up cannot be negative.");
        }

        return (Settings.Mode, target) switch
        {
            (WalletMode.Shared, TopUpTarget.Shared) => this with { Pc = Pc.Add(realToman, giftToman) },
            (WalletMode.Separate, TopUpTarget.Pc) => this with { Pc = Pc.Add(realToman, giftToman) },
            (WalletMode.Separate, TopUpTarget.NonPc) => this with { NonPc = NonPc.Add(realToman, giftToman) },
            _ => throw new BillingException(
                "invalid_topup",
                $"Top-up target {target} does not match wallet mode {Settings.Mode}."),
        };
    }

    /// <summary>
    /// Charges a console or table invoice to the wallet that pays those stations.
    /// Shared mode uses the one wallet. Separate mode uses <see cref="NonPc"/> and leaves the PC balance alone.
    /// </summary>
    public NonPcCharge ChargeNonPc(long amountToman)
    {
        Settings.EnsureValid();
        if (amountToman < 0)
        {
            throw new BillingException("invalid_money", "A console charge cannot be negative.");
        }

        var pocket = Settings.Mode == WalletMode.Shared ? Pc : NonPc;
        if (!pocket.TrySpend(amountToman, Settings.Debt, out var updated, out var giftDrawn, out var realDrawn))
        {
            return new NonPcCharge(false, this, 0, 0);
        }

        var account = Settings.Mode == WalletMode.Shared
            ? this with { Pc = updated }
            : this with { NonPc = updated };
        return new NonPcCharge(true, account, giftDrawn, realDrawn);
    }
}

public readonly record struct NonPcCharge(bool Accepted, CustomerAccount Account, long GiftDrawnToman, long RealDrawnToman);

public sealed record PcPlayResult(Settlement Settlement, CustomerAccount Account);

public static class CustomerPlay
{
    public static PcPlayResult Settle(SessionCharge charge, CustomerAccount account, AccountPolicy? policy = null)
    {
        account.Settings.EnsureValid();
        var money = account.Pc.TotalToman;
        if (BlocksPackages(account, money))
        {
            var blocked = AccountSettlement.Settle(
                charge,
                new PcAccount(account.Id, money, account.Packages),
                policy);
            return new PcPlayResult(blocked, account);
        }

        var spendable = money > 0 ? money : 0;
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount(account.Id, spendable, account.Packages),
            policy,
            graceMinuteEnabled: account.Settings.GraceMinuteEnabled);
        var pocket = account.Pc;
        if (settled.MoneyConsumedToman > 0
            && !pocket.TrySpend(settled.MoneyConsumedToman, DebtPolicy.None, out pocket, out _, out _))
        {
            throw new BillingException("invalid_money", "PC charge exceeded the positive balance.");
        }

        var updated = account with
        {
            Pc = pocket,
            Packages = settled.UpdatedAccount.Packages,
        };
        var visible = settled with
        {
            UpdatedAccount = new PcAccount(account.Id, pocket.TotalToman, settled.UpdatedAccount.Packages),
        };
        return new PcPlayResult(visible, updated);
    }

    public static RemainingTimeEstimate Estimate(
        CustomerAccount account,
        string groupId,
        PlayMode mode,
        int playerCount,
        DateTimeOffset at,
        RateCard rates,
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null)
    {
        account.Settings.EnsureValid();
        var money = account.Pc.TotalToman;
        if (BlocksPackages(account, money))
        {
            return RemainingTimeEstimator.Estimate(
                new PcAccount(account.Id, money, account.Packages),
                groupId,
                mode,
                playerCount,
                at,
                rates,
                specialDays,
                policy);
        }

        var spendable = money > 0 ? money : 0;
        var estimate = RemainingTimeEstimator.Estimate(
            new PcAccount(account.Id, spendable, account.Packages),
            groupId,
            mode,
            playerCount,
            at,
            rates,
            specialDays,
            policy,
            account.Settings.GraceMinuteEnabled);
        return estimate with { MoneyBalanceToman = money };
    }

    private static bool BlocksPackages(CustomerAccount account, long pcMoney)
    {
        return account.Settings.Mode == WalletMode.Shared
            && pcMoney < 0
            && !account.Settings.AllowPcWhileInDebt;
    }
}

internal static class WalletPocketMath
{
    public static WalletPocket Add(this WalletPocket pocket, long realToman, long giftToman)
    {
        return new WalletPocket(pocket.RealToman + realToman, pocket.GiftToman + giftToman);
    }
}
