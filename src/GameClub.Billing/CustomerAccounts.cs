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
/// <see cref="Debt"/> is how far a console or table charge may go. The default is no debt.
/// PC play never uses it to go further into debt.
/// <see cref="ApplyDiscountsToPc"/> defaults to false. When it is true, level, birthday, and
/// code discounts reduce the PC debit and the remaining-time estimate. The grace threshold
/// is the discounted price of the next minute. A PC balance is never rounded.
/// </summary>
public sealed record WalletSettings(
    WalletMode Mode,
    bool AllowPcWhileInDebt,
    DebtPolicy Debt,
    bool GraceMinuteEnabled = true,
    bool ApplyDiscountsToPc = false)
{
    public WalletSettings(WalletMode mode, bool allowPcWhileInDebt = false)
        : this(mode, allowPcWhileInDebt, DebtPolicy.None, true, false)
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
                BillingErrors.InvalidPolicy,
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
            throw new BillingException(BillingErrors.InvalidTopUp, "A top-up cannot be negative.");
        }

        return (Settings.Mode, target) switch
        {
            (WalletMode.Shared, TopUpTarget.Shared) => this with { Pc = Pc.Add(realToman, giftToman) },
            (WalletMode.Separate, TopUpTarget.Pc) => this with { Pc = Pc.Add(realToman, giftToman) },
            (WalletMode.Separate, TopUpTarget.NonPc) => this with { NonPc = NonPc.Add(realToman, giftToman) },
            _ => throw new BillingException(
                BillingErrors.InvalidTopUp,
                $"Top-up target {target} does not match wallet mode {Settings.Mode}."),
        };
    }

    /// <summary>
    /// Charges a console or table invoice to the wallet that pays those stations.
    /// Shared mode uses the one wallet. Separate mode uses <see cref="NonPc"/> and leaves the PC balance alone.
    /// The pocket stops at <see cref="WalletSettings.Debt"/>. The default debt policy is none.
    /// </summary>
    public NonPcCharge ChargeNonPc(long amountToman)
    {
        Settings.EnsureValid();
        if (amountToman < 0)
        {
            throw new BillingException(BillingErrors.InvalidMoney, "A console charge cannot be negative.");
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

public sealed record PcPlayResult(
    Settlement Settlement,
    CustomerAccount Account,
    long GiftDrawnToman = 0,
    long RealDrawnToman = 0,
    Invoice? ChargeInvoice = null);

public static class CustomerPlay
{
    public static PcPlayResult Settle(
        SessionCharge charge,
        CustomerAccount account,
        AccountPolicy? policy = null,
        BillingPolicy? billing = null,
        CustomerPricing? pricing = null,
        DiscountCode? code = null,
        DiscountPolicy? discounts = null,
        DateTimeOffset? issuedAt = null,
        RoundingPolicy? rounding = null)
    {
        account.Settings.EnsureValid();
        billing ??= BillingPolicy.Default;
        var money = account.Pc.TotalToman;
        if (BlocksPackages(account, money))
        {
            var blocked = AccountSettlement.Settle(
                charge,
                new PcAccount(account.Id, money, account.Packages),
                policy,
                billing,
                graceMinuteEnabled: false);
            return new PcPlayResult(blocked, account);
        }

        var timeDiscount = default(PcTimeDiscount);
        if (account.Settings.ApplyDiscountsToPc && charge.Lines.Count > 0)
        {
            timeDiscount = DiscountFor(pricing, code, discounts, charge.Lines[0].GroupId, charge.Lines[0].Start);
        }

        var inDebt = money < 0;
        var spendable = money > 0 ? money : 0;
        var settled = AccountSettlement.Settle(
            charge,
            new PcAccount(account.Id, spendable, account.Packages),
            policy,
            billing,
            account.Settings.GraceMinuteEnabled && !inDebt,
            timeDiscount,
            shortfallIsUncovered: inDebt);

        var debit = settled.MoneyConsumedToman;
        Invoice? invoice = null;
        var graceWaived = settled.Grace?.WaivedToman ?? 0;
        if (debit > 0 || graceWaived > 0 || settled.WriteOffToman > 0 || settled.DiscountToman > 0)
        {
            var when = issuedAt ?? (charge.Lines.Count > 0 ? charge.Lines[charge.Lines.Count - 1].End : default);
            if (when == default)
            {
                throw new BillingException(BillingErrors.InvalidInvoice, "A PC charge needs an invoice time.");
            }

            when = TehranTime.Ensure(when);
            invoice = PcInvoice(charge, settled, when, rounding);
            if (invoice.PayableToman != debit)
            {
                throw new BillingException(BillingErrors.InvalidMoney, "PC invoice payable does not match the balance debit.");
            }
        }

        var pocket = account.Pc;
        long giftDrawn = 0;
        long realDrawn = 0;
        if (debit > 0 && !pocket.TrySpend(debit, DebtPolicy.None, out pocket, out giftDrawn, out realDrawn))
        {
            throw new BillingException(BillingErrors.InvalidMoney, "PC charge exceeded the positive balance.");
        }

        var updated = account with
        {
            Pc = pocket,
            Packages = settled.UpdatedAccount.Packages,
        };
        var visible = settled with
        {
            MoneyConsumedToman = debit,
            UpdatedAccount = new PcAccount(account.Id, pocket.TotalToman, settled.UpdatedAccount.Packages),
        };
        return new PcPlayResult(visible, updated, giftDrawn, realDrawn, invoice);
    }

    private static Invoice PcInvoice(SessionCharge charge, Settlement settled, DateTimeOffset when, RoundingPolicy? rounding)
    {
        var grace = settled.Grace?.WaivedToman ?? 0;
        var moneyGross = charge.TotalAmountToman - settled.PackageCoveredToman;
        if (moneyGross < 0)
        {
            moneyGross = 0;
        }

        var date = TehranTime.Date(when);
        var lines = new List<InvoiceLine>
        {
            new(InvoiceLineKind.Time, "PC time", moneyGross, date),
        };
        if (settled.DiscountToman > 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Discount, "PC discount", -settled.DiscountToman, date));
        }

        if (grace > 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.Grace, "Grace minute", -grace, date));
        }

        if (settled.WriteOffToman > 0)
        {
            lines.Add(new InvoiceLine(InvoiceLineKind.WriteOff, "Written off", -settled.WriteOffToman, date));
        }

        var payable = moneyGross - settled.DiscountToman - grace - settled.WriteOffToman - settled.UncoveredToman;
        if (payable < 0)
        {
            payable = 0;
        }

        // A prepaid PC invoice is paid from the PC balance. That balance is never rounded;
        // pilot rounding (step 5,000, up) applies only to a counter remainder, which is zero here.
        if (rounding is { Enabled: true } && payable < 0)
        {
            throw new BillingException(BillingErrors.InvalidMoney, "PC invoice payable cannot be negative.");
        }

        var net = moneyGross - grace - settled.DiscountToman - settled.WriteOffToman;
        return new Invoice(lines, moneyGross, 0, 0, settled.DiscountToman, net, 0, payable, null, grace);
    }

    public static RemainingTimeEstimate Estimate(
        CustomerAccount account,
        string groupId,
        PlayMode mode,
        int playerCount,
        DateTimeOffset at,
        RateCard rates,
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null,
        CustomerPricing? pricing = null,
        DiscountCode? code = null,
        DiscountPolicy? discounts = null)
    {
        account.Settings.EnsureValid();
        var money = account.Pc.TotalToman;
        PcTimeDiscount? timeDiscount = null;
        if (account.Settings.ApplyDiscountsToPc)
        {
            timeDiscount = DiscountFor(pricing, code, discounts, groupId, at);
        }

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
            account.Settings.GraceMinuteEnabled,
            timeDiscount);
        return estimate with { MoneyBalanceToman = money };
    }

    internal static PcTimeDiscount DiscountFor(
        CustomerPricing? pricing,
        DiscountCode? code,
        DiscountPolicy? discounts,
        string groupId,
        DateTimeOffset at)
    {
        var policy = discounts ?? DiscountPolicy.Default;
        policy.Birthday.EnsureValid();
        var level = pricing?.LevelPercent ?? 0;
        var birthday = 0;
        if (pricing?.BirthDate is JalaliDate birth && !birth.IsEmpty && pricing.BirthdayPercent > 0)
        {
            if (policy.Birthday.Includes(birth, TehranTime.Date(at)))
            {
                birthday = pricing.BirthdayPercent;
            }
        }

        var combined = level + birthday;
        if (combined > policy.MaxCombinedPercent)
        {
            combined = policy.MaxCombinedPercent;
        }

        if (combined < 0)
        {
            combined = 0;
        }

        var codePercent = 0;
        long fixedToman = 0;
        if (code is not null && code.Scope.HasFlag(ChargeScope.Time) && CodeApplies(code, groupId, at))
        {
            codePercent = code.Percent;
            fixedToman = code.FixedToman;
        }

        return new PcTimeDiscount(combined, codePercent, fixedToman);
    }

    private static bool CodeApplies(DiscountCode code, string groupId, DateTimeOffset at)
    {
        at = TehranTime.Ensure(at);
        if (code.ValidFrom is DateTimeOffset from && at < TehranTime.Ensure(from))
        {
            return false;
        }

        if (code.ValidUntil is DateTimeOffset until && at >= TehranTime.Ensure(until))
        {
            return false;
        }

        if (code.AllowedGroupIds is { Count: > 0 } groups && !groups.Contains(groupId))
        {
            return false;
        }

        if (code.AllowedWeekdays is { Count: > 0 } days && !days.Contains(TehranTime.Date(at).DayOfWeek))
        {
            return false;
        }

        return true;
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
        var real = MoneyMath.CheckedAdd(pocket.RealToman, realToman);
        var gift = MoneyMath.CheckedAdd(pocket.GiftToman, giftToman);
        return new WalletPocket(real, gift);
    }
}
