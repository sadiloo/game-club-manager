namespace GameClub.Billing;

/// <summary>
/// A prepaid block of minutes. The face value is informational (for example
/// "2 hours = 200,000 toman"); consumption is in minutes, not in that money.
/// One minute of package time covers one billed minute in an allowed group,
/// regardless of that group's hourly rate. <see cref="ValidThrough"/> is inclusive.
/// Hour window is half-open <c>[AllowedFrom, AllowedUntil)</c> and may wrap midnight.
/// </summary>
public sealed record TimePackage(
    string Id,
    long RemainingMinutes,
    long? FaceValueToman = null,
    IReadOnlySet<string>? AllowedGroupIds = null,
    TimeOnly? AllowedFrom = null,
    TimeOnly? AllowedUntil = null,
    DateOnly? ValidThrough = null);

public sealed record PcAccount(string Id, long MoneyBalanceToman, IReadOnlyList<TimePackage> Packages);

public enum ConsumptionOrder
{
    /// <summary>Default. Package minutes are spent before wallet money.</summary>
    PackagesThenMoney = 0,

    /// <summary>Wallet money is spent before package minutes.</summary>
    MoneyThenPackages = 1,
}

/// <summary>
/// How far a console or table wallet charge may push a balance below zero.
/// PC play never uses this to go further negative. Null <see cref="MaxDebtToman"/> means unlimited.
/// Zero refuses to go below zero. A positive value is the deepest allowed debt.
/// </summary>
public sealed record DebtPolicy(long? MaxDebtToman)
{
    public static DebtPolicy Unlimited { get; } = new((long?)null);

    public static DebtPolicy None { get; } = new(0);

    public static DebtPolicy Limited(long maxDebtToman)
    {
        if (maxDebtToman < 0)
        {
            throw new BillingException("invalid_policy", "Max debt cannot be negative. Use null for unlimited debt.");
        }

        return new DebtPolicy(maxDebtToman);
    }
}

/// <summary>
/// PC sessions always stop at a zero balance. Debt is not a PC-account option;
/// a shared wallet that is already negative blocks PC play instead of going further into debt.
/// </summary>
public sealed record AccountPolicy(ConsumptionOrder Order)
{
    public static AccountPolicy Default { get; } = new(ConsumptionOrder.PackagesThenMoney);
}

public sealed record PackageDraw(string PackageId, string SegmentId, DateTimeOffset SliceStart, long Minutes);

public sealed record MoneyDraw(string SegmentId, DateTimeOffset SliceStart, long Minutes, long AmountToman);

public sealed record Settlement(
    long GrossToman,
    long PackageMinutesConsumed,
    long PackageCoveredToman,
    long MoneyConsumedToman,
    long UncoveredToman,
    PcAccount UpdatedAccount,
    IReadOnlyList<PackageDraw> PackageDraws,
    IReadOnlyList<MoneyDraw> MoneyDraws)
{
    /// <summary>Time lines the customer still owes, for an invoice that must not charge package minutes again.</summary>
    public IReadOnlyList<TimeChargeLine> MoneyTimeLines()
    {
        return MoneyDraws
            .Where(draw => draw.AmountToman > 0)
            .Select(draw => new TimeChargeLine(
                $"Account time {draw.SegmentId}",
                draw.AmountToman,
                null,
                TehranTime.Date(draw.SliceStart),
                TehranTime.Date(draw.SliceStart).DayOfWeek))
            .ToArray();
    }

    /// <summary>How much of the balance is debt. Zero when the account is not negative.</summary>
    public long DebtToman => UpdatedAccount.MoneyBalanceToman < 0 ? -UpdatedAccount.MoneyBalanceToman : 0;
}

public sealed record RemainingTimeEstimate(
    string GroupId,
    PlayMode Mode,
    int PlayerCount,
    DateTimeOffset At,
    long HourlyRateToman,
    RateSource Source,
    bool RateIsFree,
    long EligiblePackageMinutes,
    long MoneyBalanceToman,
    long? MoneyMinutes,
    long TotalMinutes,
    bool BlockedByDebt = false);

/// <summary>
/// Applies a priced session to a PC account. The balance never goes negative: money
/// still due after packages is uncovered and the station should log the client out.
/// A balance that is already negative (console debt on the shared wallet) blocks the
/// session: packages are not consumed and the balance is left unchanged.
/// </summary>
public static class AccountSettlement
{
    public static Settlement Settle(SessionCharge charge, PcAccount account, AccountPolicy? policy = null)
    {
        policy ??= AccountPolicy.Default;
        Validate(account);
        var remaining = account.Packages.ToDictionary(package => package.Id, package => package.RemainingMinutes, StringComparer.Ordinal);
        var order = account.Packages
            .Select((package, index) => (package, index))
            .OrderBy(item => item.package.ValidThrough ?? DateOnly.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.package)
            .ToArray();

        long money = account.MoneyBalanceToman;
        if (money < 0)
        {
            return new Settlement(
                charge.TotalAmountToman,
                0,
                0,
                0,
                charge.TotalAmountToman,
                account,
                [],
                []);
        }

        long packageMinutes = 0;
        long packageCovered = 0;
        long moneyConsumed = 0;
        long uncovered = 0;
        var packageDraws = new List<PackageDraw>();
        var moneyDraws = new List<MoneyDraw>();

        foreach (var slice in charge.Lines)
        {
            foreach (var part in Divide(slice, order))
            {
                ApplyPart(part, order, remaining, policy, ref money, ref packageMinutes, ref packageCovered, ref moneyConsumed, ref uncovered, packageDraws, moneyDraws);
            }
        }

        var updatedPackages = account.Packages
            .Select(package => package with { RemainingMinutes = remaining[package.Id] })
            .ToArray();

        return new Settlement(
            charge.TotalAmountToman,
            packageMinutes,
            packageCovered,
            moneyConsumed,
            uncovered,
            new PcAccount(account.Id, money, updatedPackages),
            packageDraws,
            moneyDraws);
    }

    private static void ApplyPart(
        SlicePart part,
        IReadOnlyList<TimePackage> order,
        Dictionary<string, long> remaining,
        AccountPolicy policy,
        ref long money,
        ref long packageMinutes,
        ref long packageCovered,
        ref long moneyConsumed,
        ref long uncovered,
        List<PackageDraw> packageDraws,
        List<MoneyDraw> moneyDraws)
    {
        if (part.BilledMinutes == 0 && part.AmountToman == 0)
        {
            return;
        }

        // Dust with no billed minute cannot be paid with a package minute.
        if (part.BilledMinutes == 0)
        {
            var before = moneyConsumed;
            ChargeUpToZero(ref money, part.AmountToman, ref moneyConsumed, ref uncovered);
            var dustPaid = moneyConsumed - before;
            if (dustPaid > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, 0, dustPaid));
            }

            return;
        }

        if (part.AmountToman == 0)
        {
            // Free time must not burn a package or the wallet.
            return;
        }

        if (policy.Order == ConsumptionOrder.PackagesThenMoney)
        {
            var taken = DrawPackages(part, part.BilledMinutes, order, remaining, packageDraws);
            var coveredValue = taken >= part.BilledMinutes
                ? part.AmountToman
                : part.AmountToman * taken / part.BilledMinutes;
            var moneyDue = part.AmountToman - coveredValue;
            packageMinutes += taken;
            packageCovered += coveredValue;
            var before = moneyConsumed;
            ChargeUpToZero(ref money, moneyDue, ref moneyConsumed, ref uncovered);
            var paid = moneyConsumed - before;
            if (paid > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, part.BilledMinutes - taken, paid));
            }
        }
        else
        {
            var spendable = money > 0 ? money : 0;
            var moneyMinutes = MaxMoneyMinutes(part.AmountToman, part.BilledMinutes, spendable);
            var moneyShare = MoneyShare(part.AmountToman, moneyMinutes, part.BilledMinutes);
            money -= moneyShare;
            moneyConsumed += moneyShare;
            if (moneyShare > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, moneyMinutes, moneyShare));
            }

            var tailMinutes = part.BilledMinutes - moneyMinutes;
            var tailAmount = part.AmountToman - moneyShare;
            var taken = DrawPackages(part, tailMinutes, order, remaining, packageDraws);
            long coveredValue;
            long shortfall;
            if (taken >= tailMinutes)
            {
                coveredValue = tailAmount;
                shortfall = 0;
            }
            else
            {
                coveredValue = tailMinutes == 0 ? 0 : tailAmount * taken / tailMinutes;
                shortfall = tailAmount - coveredValue;
            }

            packageMinutes += taken;
            packageCovered += coveredValue;
            var beforeDebt = moneyConsumed;
            ChargeUpToZero(ref money, shortfall, ref moneyConsumed, ref uncovered);
            var debtPaid = moneyConsumed - beforeDebt;
            if (debtPaid > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, tailMinutes - taken, debtPaid));
            }
        }
    }

    /// <summary>PC money stops at zero. A non-positive balance pays nothing and leaves the remainder uncovered.</summary>
    private static void ChargeUpToZero(ref long balance, long due, ref long consumed, ref long uncovered)
    {
        if (due <= 0)
        {
            return;
        }

        var paid = balance > 0 ? Math.Min(balance, due) : 0;
        balance -= paid;
        consumed += paid;
        uncovered += due - paid;
    }

    private static long DrawPackages(
        SlicePart part,
        long maxMinutes,
        IReadOnlyList<TimePackage> order,
        Dictionary<string, long> remaining,
        List<PackageDraw> draws)
    {
        long left = maxMinutes;
        if (left <= 0)
        {
            return 0;
        }

        long taken = 0;
        foreach (var package in order)
        {
            if (left == 0)
            {
                break;
            }

            if (!Covers(package, part))
            {
                continue;
            }

            var available = remaining[package.Id];
            var draw = Math.Min(available, left);
            if (draw <= 0)
            {
                continue;
            }

            remaining[package.Id] = available - draw;
            left -= draw;
            taken += draw;
            draws.Add(new PackageDraw(package.Id, part.SegmentId, part.Start, draw));
        }

        return taken;
    }

    private static bool Covers(TimePackage package, SlicePart part)
    {
        if (package.AllowedGroupIds is { Count: > 0 } groups && !groups.Contains(part.GroupId))
        {
            return false;
        }

        var date = TehranTime.Date(part.Start);
        if (package.ValidThrough is DateOnly until && date > until)
        {
            return false;
        }

        if (package.AllowedFrom is TimeOnly from && package.AllowedUntil is TimeOnly to)
        {
            var start = TehranTime.TimeOfDay(part.Start);
            var justBeforeEnd = part.End.AddTicks(-1);
            if (justBeforeEnd < part.Start)
            {
                return false;
            }

            var endTime = TehranTime.TimeOfDay(justBeforeEnd);
            return InWindow(start, from, to) && InWindow(endTime, from, to);
        }

        return true;
    }

    private static bool InWindow(TimeOnly time, TimeOnly from, TimeOnly until)
    {
        if (from < until)
        {
            return time >= from && time < until;
        }

        if (from > until)
        {
            return time >= from || time < until;
        }

        return false;
    }

    private static long MoneyShare(long amount, long minutes, long billed)
    {
        if (minutes <= 0 || amount <= 0 || billed <= 0)
        {
            return 0;
        }

        if (minutes >= billed)
        {
            return amount;
        }

        return amount * minutes / billed;
    }

    private static long MaxMoneyMinutes(long amount, long billed, long balance)
    {
        if (balance <= 0 || amount <= 0 || billed <= 0)
        {
            return 0;
        }

        if (amount <= balance)
        {
            return billed;
        }

        var minutes = balance * billed / amount;
        if (minutes > billed)
        {
            minutes = billed;
        }

        while (minutes < billed && MoneyShare(amount, minutes + 1, billed) <= balance)
        {
            minutes++;
        }

        while (minutes > 0 && MoneyShare(amount, minutes, billed) > balance)
        {
            minutes--;
        }

        return minutes;
    }

    private static IReadOnlyList<SlicePart> Divide(PricedSlice slice, IReadOnlyList<TimePackage> packages)
    {
        var marks = new SortedSet<DateTimeOffset> { slice.Start, slice.End };
        var date = TehranTime.Date(slice.Start);
        foreach (var package in packages)
        {
            if (package.AllowedFrom is TimeOnly from)
            {
                AddMark(marks, slice, TehranTime.At(date, from));
            }

            if (package.AllowedUntil is TimeOnly until)
            {
                AddMark(marks, slice, TehranTime.At(date, until));
            }
        }

        var points = marks.ToArray();
        if (points.Length == 2)
        {
            return [new SlicePart(slice.SegmentId, slice.GroupId, slice.Start, slice.End, slice.ActiveSeconds, slice.BilledMinutes, slice.AmountToman)];
        }

        var raw = new List<(DateTimeOffset Start, DateTimeOffset End, long Seconds)>();
        for (var i = 0; i < points.Length - 1; i++)
        {
            var seconds = TehranTime.WholeSeconds(points[i], points[i + 1]);
            if (seconds > 0)
            {
                raw.Add((points[i], points[i + 1], seconds));
            }
        }

        if (raw.Count == 0 || slice.ActiveSeconds <= 0)
        {
            return [new SlicePart(slice.SegmentId, slice.GroupId, slice.Start, slice.End, slice.ActiveSeconds, slice.BilledMinutes, slice.AmountToman)];
        }

        var parts = new SlicePart[raw.Count];
        long usedMinutes = 0;
        long usedAmount = 0;
        for (var i = 0; i < raw.Count; i++)
        {
            long minutes;
            long amount;
            if (i == raw.Count - 1)
            {
                minutes = slice.BilledMinutes - usedMinutes;
                amount = slice.AmountToman - usedAmount;
            }
            else
            {
                minutes = slice.BilledMinutes * raw[i].Seconds / slice.ActiveSeconds;
                amount = slice.AmountToman * raw[i].Seconds / slice.ActiveSeconds;
                usedMinutes += minutes;
                usedAmount += amount;
            }

            parts[i] = new SlicePart(slice.SegmentId, slice.GroupId, raw[i].Start, raw[i].End, raw[i].Seconds, minutes, amount);
        }

        return parts;
    }

    private static void AddMark(SortedSet<DateTimeOffset> marks, PricedSlice slice, DateTimeOffset mark)
    {
        if (mark > slice.Start && mark < slice.End)
        {
            marks.Add(mark);
        }
    }

    private static void Validate(PcAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Id))
        {
            throw new BillingException("invalid_account", "Account id is required.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in account.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id) || !ids.Add(package.Id))
            {
                throw new BillingException("invalid_account", $"Package id '{package.Id}' is missing or duplicated.");
            }

            if (package.RemainingMinutes < 0)
            {
                throw new BillingException("invalid_account", $"Package '{package.Id}' has negative minutes.");
            }

            if (package.FaceValueToman is < 0)
            {
                throw new BillingException("invalid_money", $"Package '{package.Id}' has a negative face value.");
            }

            var hasFrom = package.AllowedFrom is not null;
            var hasUntil = package.AllowedUntil is not null;
            if (hasFrom != hasUntil)
            {
                throw new BillingException("invalid_account", $"Package '{package.Id}' must set both window ends or neither.");
            }

            if (package.AllowedFrom is TimeOnly from && package.AllowedUntil is TimeOnly until && from == until)
            {
                throw new BillingException("invalid_account", $"Package '{package.Id}' has an empty hour window.");
            }
        }
    }

    private sealed record SlicePart(
        string SegmentId,
        string GroupId,
        DateTimeOffset Start,
        DateTimeOffset End,
        long ActiveSeconds,
        long BilledMinutes,
        long AmountToman);
}

public static class RemainingTimeEstimator
{
    /// <summary>
    /// Estimate of play time if the member starts now on this group, mode, and player count.
    /// Package minutes that are eligible at <paramref name="at"/> are counted 1:1.
    /// Money is converted at the rate in force at <paramref name="at"/> only. A zero balance
    /// contributes no money minutes, so the client can log out when the estimate hits zero.
    /// A negative shared balance blocks PC play: <see cref="RemainingTimeEstimate.TotalMinutes"/>
    /// is zero and packages are not counted until the debt is cleared. A zero hourly rate is free.
    /// </summary>
    public static RemainingTimeEstimate Estimate(
        PcAccount account,
        string groupId,
        PlayMode mode,
        int playerCount,
        DateTimeOffset at,
        RateCard rates,
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null)
    {
        specialDays ??= SpecialDayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        if (account.MoneyBalanceToman < 0)
        {
            return new RemainingTimeEstimate(
                groupId,
                mode,
                playerCount,
                at,
                0,
                RateSource.BaseTariff,
                false,
                0,
                account.MoneyBalanceToman,
                0,
                0,
                true);
        }

        var quote = RateResolver.Resolve(rates, specialDays, at, groupId, mode, playerCount);
        long packageMinutes = 0;
        foreach (var package in account.Packages)
        {
            if (package.RemainingMinutes <= 0)
            {
                continue;
            }

            if (package.AllowedGroupIds is { Count: > 0 } groups && !groups.Contains(groupId))
            {
                continue;
            }

            if (package.ValidThrough is DateOnly until && TehranTime.Date(at) > until)
            {
                continue;
            }

            if (package.AllowedFrom is TimeOnly from && package.AllowedUntil is TimeOnly to)
            {
                if (!InWindow(TehranTime.TimeOfDay(at), from, to))
                {
                    continue;
                }
            }

            packageMinutes += package.RemainingMinutes;
        }

        var free = quote.HourlyRateToman == 0;
        long? moneyMinutes = free
            ? null
            : account.MoneyBalanceToman <= 0
                ? 0
                : MoneyMath.MaxAffordableMinutes(quote.HourlyRateToman, account.MoneyBalanceToman, policy);

        return new RemainingTimeEstimate(
            groupId,
            mode,
            playerCount,
            at,
            quote.HourlyRateToman,
            quote.Source,
            free,
            packageMinutes,
            account.MoneyBalanceToman,
            moneyMinutes,
            packageMinutes + (moneyMinutes ?? 0));
    }

    private static bool InWindow(TimeOnly time, TimeOnly from, TimeOnly until)
    {
        if (from < until)
        {
            return time >= from && time < until;
        }

        return time >= from || time < until;
    }
}
