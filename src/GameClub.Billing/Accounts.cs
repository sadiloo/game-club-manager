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
    IReadOnlyCollection<string>? AllowedGroupIds = null,
    TimeOfDay? AllowedFrom = null,
    TimeOfDay? AllowedUntil = null,
    LocalDate? ValidThrough = null);

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

/// <summary>
/// One grace minute. <see cref="WaivedToman"/> is the part of that minute the balance could not
/// cover. It is not charged and it is not debt. The balance pays only what it had.
/// </summary>
public sealed record GraceLine(long Minutes, long WaivedToman, DateTimeOffset At, string? SegmentId);

/// <summary>Operator-visible notice. PC overshoot uses <c>pc_overshoot</c>. It is not a debt.</summary>
public sealed record OperatorAlert(string Code, string Message, long AmountToman);

/// <summary>One shift or daily report row. <see cref="Kind"/> is <c>write_off</c> for PC overshoot.</summary>
public sealed record DailyReportEntry(LocalDate Date, string Kind, long AmountToman, string? SegmentId);

public sealed record Settlement(
    long GrossToman,
    long PackageMinutesConsumed,
    long PackageCoveredToman,
    long MoneyConsumedToman,
    long UncoveredToman,
    PcAccount UpdatedAccount,
    IReadOnlyList<PackageDraw> PackageDraws,
    IReadOnlyList<MoneyDraw> MoneyDraws,
    GraceLine? Grace = null,
    long DiscountToman = 0,
    long WriteOffToman = 0,
    OperatorAlert? Alert = null,
    DailyReportEntry? ReportEntry = null,
    long OmittedToman = 0,
    long InvoicedTimeToman = 0)
{
    /// <summary>
    /// Time lines the customer still owes, at gross (paid + grace + discount), so a later grace
    /// line waives that minute once. Package minutes are not included.
    /// </summary>
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
/// Immutable PC discount spec. A percent is taken once on the running total and a fixed
/// code once. Birthday, weekday, and validity windows use each minute's Tehran time.
/// </summary>
public sealed record SessionDiscount(
    int LevelPercent = 0,
    int BirthdayPercent = 0,
    int CodePercent = 0,
    long FixedToman = 0,
    int Cap = 100,
    JalaliDate? BirthDate = null,
    BirthdayWindow? Birthday = null,
    IReadOnlyCollection<DayOfWeek>? CodeWeekdays = null,
    IReadOnlyCollection<string>? CodeGroups = null,
    DateTimeOffset? ValidFrom = null,
    DateTimeOffset? ValidUntil = null)
{
    public static SessionDiscount Empty { get; } = new();
}

internal sealed class DiscountLedger
{
    private readonly SessionDiscount _spec;

    public DiscountLedger(SessionDiscount spec)
    {
        _spec = spec;
    }

    public long All { get; private set; }

    public long CodeGross { get; private set; }

    public long BirthGross { get; private set; }

    public bool CodeOn(DateTimeOffset at, string? groupId)
    {
        if (_spec.CodePercent <= 0 && _spec.FixedToman <= 0)
        {
            return false;
        }

        if (_spec.ValidFrom is DateTimeOffset from && at < TehranTime.Ensure(from))
        {
            return false;
        }

        if (_spec.ValidUntil is DateTimeOffset until && at >= TehranTime.Ensure(until))
        {
            return false;
        }

        if (_spec.CodeGroups is { Count: > 0 } groups && (groupId is null || !groups.Contains(groupId)))
        {
            return false;
        }

        var date = TehranTime.Date(at);
        return _spec.CodeWeekdays is not { Count: > 0 } days || days.Contains(date.DayOfWeek);
    }

    public bool BirthOn(DateTimeOffset at)
    {
        return _spec.BirthdayPercent > 0
            && _spec.BirthDate is JalaliDate birth
            && !birth.IsEmpty
            && (_spec.Birthday ?? BirthdayWindow.ExactDay).Includes(birth, TehranTime.Date(at));
    }

    public long NetNow() => Compute(All, CodeGross, BirthGross);

    public long NetOf(long gross, DateTimeOffset at, string? groupId)
    {
        if (gross <= 0)
        {
            return 0;
        }

        return Compute(gross, CodeOn(at, groupId) ? gross : 0, BirthOn(at) ? gross : 0);
    }

    public long Marginal(long add, DateTimeOffset at, string? groupId)
    {
        if (add <= 0)
        {
            return 0;
        }

        if (_spec.LevelPercent == 0 && _spec.BirthdayPercent == 0 && _spec.CodePercent == 0 && _spec.FixedToman == 0)
        {
            return add;
        }

        var next = Compute(
            All + add,
            CodeGross + (CodeOn(at, groupId) ? add : 0),
            BirthGross + (BirthOn(at) ? add : 0));
        var delta = next - NetNow();
        return delta < 0 ? 0 : delta;
    }

    public void Commit(long add, DateTimeOffset at, string? groupId)
    {
        if (add <= 0)
        {
            return;
        }

        All += add;
        if (CodeOn(at, groupId))
        {
            CodeGross += add;
        }

        if (BirthOn(at))
        {
            BirthGross += add;
        }
    }

    private long Compute(long all, long codeG, long birthG)
    {
        long levelOff;
        long birthOff;
        if (_spec.BirthdayPercent > 0 && birthG > 0)
        {
            var sum = _spec.LevelPercent + _spec.BirthdayPercent;
            var rest = all - birthG;
            if (rest < 0)
            {
                rest = 0;
            }

            if (sum <= _spec.Cap)
            {
                levelOff = MoneyMath.PercentOf(rest, _spec.LevelPercent) + MoneyMath.PercentOf(birthG, _spec.LevelPercent);
                birthOff = MoneyMath.PercentOf(birthG, _spec.BirthdayPercent);
            }
            else
            {
                var combined = MoneyMath.PercentOf(birthG, _spec.Cap);
                var levelShare = sum == 0 ? 0 : MoneyMath.MulDiv(combined, _spec.LevelPercent, sum);
                levelOff = MoneyMath.PercentOf(rest, _spec.LevelPercent) + levelShare;
                birthOff = combined - levelShare;
            }
        }
        else
        {
            levelOff = MoneyMath.PercentOf(all, _spec.LevelPercent);
            birthOff = 0;
        }

        var codeOff = MoneyMath.PercentOf(codeG, _spec.CodePercent);
        long fixedOff = 0;
        if (_spec.FixedToman > 0 && codeG > 0)
        {
            var room = codeG - codeOff;
            if (room < 0)
            {
                room = 0;
            }

            fixedOff = _spec.FixedToman < room ? _spec.FixedToman : room;
        }

        var net = all - levelOff - birthOff - codeOff - fixedOff;
        return net < 0 ? 0 : net;
    }
}

/// <summary>
/// Applies a priced session to a PC account. The balance never goes negative: money
/// still due after packages is uncovered and the station should log the client out.
/// A balance that is already negative (console debt on the shared wallet) blocks the
/// session: packages are not consumed and the balance is left unchanged.
/// </summary>
public static class AccountSettlement
{
    public static Settlement Settle(
        SessionCharge charge,
        PcAccount account,
        AccountPolicy? policy = null,
        BillingPolicy? billing = null,
        bool graceMinuteEnabled = true,
        SessionDiscount? discount = null,
        bool shortfallIsUncovered = false)
    {
        policy ??= AccountPolicy.Default;
        billing ??= BillingPolicy.Default;
        billing.EnsureValid();
        Validate(account);
        var remaining = account.Packages.ToDictionary(package => package.Id, package => package.RemainingMinutes, StringComparer.Ordinal);
        var order = account.Packages
            .Select((package, index) => (package, index))
            .OrderBy(item => item.package.ValidThrough ?? LocalDate.MaxValue)
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
        long writeOff = 0;
        long omitted = 0;
        var pricedDiscount = new DiscountLedger(discount ?? SessionDiscount.Empty);
        var grace = new GraceTracker { Enabled = graceMinuteEnabled && !shortfallIsUncovered, UncoveredShortfall = shortfallIsUncovered };
        var packageDraws = new List<PackageDraw>();
        var moneyDraws = new List<MoneyDraw>();

        foreach (var slice in charge.Lines)
        {
            var parts = Divide(slice, order, remaining);
            if (slice.ByDate is { Count: > 1 } && parts.Count == 1 && parts[0].BilledMinutes == slice.BilledMinutes)
            {
                parts = SplitByDate(slice);
            }

            foreach (var part in parts)
            {
                ApplyPart(
                    part,
                    order,
                    remaining,
                    policy,
                    billing,
                    pricedDiscount,
                    grace,
                    ref money,
                    ref packageMinutes,
                    ref packageCovered,
                    ref moneyConsumed,
                    ref uncovered,
                    ref writeOff,
                    ref omitted,
                    packageDraws,
                    moneyDraws);
            }
        }

        var updatedPackages = account.Packages
            .Select(package => package with { RemainingMinutes = remaining[package.Id] })
            .ToArray();

        OperatorAlert? alert = null;
        DailyReportEntry? report = null;
        if (writeOff > 0)
        {
            alert = new OperatorAlert("pc_overshoot", "PC time ran past the balance and was written off.", writeOff);
            if (charge.Lines.Count > 0)
            {
                var when = charge.Lines[0].Start;
                report = new DailyReportEntry(TehranTime.Date(when), "write_off", writeOff, charge.Lines[0].SegmentId);
            }
        }

        return new Settlement(
            charge.TotalAmountToman,
            packageMinutes,
            packageCovered,
            moneyConsumed,
            uncovered,
            new PcAccount(account.Id, money, updatedPackages),
            packageDraws,
            moneyDraws,
            grace.Waived > 0 ? new GraceLine(1, grace.Waived, grace.At, grace.SegmentId) : null,
            pricedDiscount.All - pricedDiscount.NetNow(),
            writeOff,
            alert,
            report,
            omitted,
            pricedDiscount.All);
    }

    private static void ApplyPart(
        SlicePart part,
        IReadOnlyList<TimePackage> order,
        Dictionary<string, long> remaining,
        AccountPolicy policy,
        BillingPolicy billing,
        DiscountLedger discount,
        GraceTracker grace,
        ref long money,
        ref long packageMinutes,
        ref long packageCovered,
        ref long moneyConsumed,
        ref long uncovered,
        ref long writeOff,
        ref long omitted,
        List<PackageDraw> packageDraws,
        List<MoneyDraw> moneyDraws)
    {
        if (part.BilledMinutes == 0 && part.AmountToman == 0)
        {
            return;
        }

        if (part.BilledMinutes == 0)
        {
            var before = moneyConsumed;
            long gross = 0;
            ApplyMoney(ref money, part.HourlyRateToman, 0, part.AmountToman, part.ActiveSeconds, billing, discount, part.Start, part.GroupId, grace, part.Start, part.SegmentId, ref moneyConsumed, ref uncovered, ref writeOff, ref omitted, ref gross);
            if (gross > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, 0, gross));
            }
            else if (moneyConsumed > before)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, 0, moneyConsumed - before));
            }

            return;
        }

        if (part.AmountToman == 0)
        {
            return;
        }

        if (policy.Order == ConsumptionOrder.PackagesThenMoney)
        {
            var taken = DrawPackages(part, part.BilledMinutes, order, remaining, packageDraws);
            var coveredValue = taken >= part.BilledMinutes
                ? part.AmountToman
                : MoneyMath.MulDiv(part.AmountToman, taken, part.BilledMinutes);
            var moneyDue = part.AmountToman - coveredValue;
            packageMinutes += taken;
            packageCovered += coveredValue;
            long gross = 0;
            var moneyActive = taken >= part.BilledMinutes ? 0 : part.ActiveSeconds;
            ApplyMoney(ref money, part.HourlyRateToman, part.BilledMinutes - taken, moneyDue, moneyActive, billing, discount, part.Start, part.GroupId, grace, part.Start, part.SegmentId, ref moneyConsumed, ref uncovered, ref writeOff, ref omitted, ref gross);
            if (gross > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, part.BilledMinutes - taken, gross));
            }
        }
        else
        {
            var spendable = money > 0 ? money : 0;
            var moneyMinutes = MaxMoneyMinutes(part.AmountToman, part.BilledMinutes, spendable);
            var moneyShare = MoneyShare(part.AmountToman, moneyMinutes, part.BilledMinutes);
            var netShare = discount.Marginal(moneyShare, part.Start, part.GroupId);
            var pay = netShare < spendable ? netShare : spendable;
            money -= pay;
            moneyConsumed += pay;
            discount.Commit(moneyShare, part.Start, part.GroupId);
            if (pay < netShare)
            {
                writeOff += netShare - pay;
            }

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
                coveredValue = tailMinutes == 0 ? 0 : MoneyMath.MulDiv(tailAmount, taken, tailMinutes);
                shortfall = tailAmount - coveredValue;
            }

            packageMinutes += taken;
            packageCovered += coveredValue;
            long gross = 0;
            ApplyMoney(ref money, part.HourlyRateToman, tailMinutes - taken, shortfall, tailMinutes * 60L, billing, discount, part.Start, part.GroupId, grace, part.Start, part.SegmentId, ref moneyConsumed, ref uncovered, ref writeOff, ref omitted, ref gross);
            if (gross > 0)
            {
                moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, tailMinutes - taken, gross));
            }
        }
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
        if (package.ValidThrough is LocalDate until && date > until)
        {
            return false;
        }

        if (package.AllowedFrom is TimeOfDay from && package.AllowedUntil is TimeOfDay to)
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

    private static bool InWindow(TimeOfDay time, TimeOfDay from, TimeOfDay until)
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

        return MoneyMath.MulDiv(amount, minutes, billed);
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

        var minutes = MoneyMath.MulDiv(balance, billed, amount);
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

    private static IReadOnlyList<SlicePart> SplitByDate(PricedSlice slice)
    {
        var dates = slice.ByDate!;
        long totalMinutes = 0;
        foreach (var part in dates)
        {
            totalMinutes += part.Minutes;
        }

        var amounts = new long[dates.Count];
        var fractions = new long[dates.Count];
        long assigned = 0;
        for (var i = 0; i < dates.Count; i++)
        {
            amounts[i] = totalMinutes == 0 ? 0 : MoneyMath.MulDiv(slice.AmountToman, dates[i].Minutes, totalMinutes);
            fractions[i] = totalMinutes == 0 ? 0 : MoneyMath.MulMod(slice.AmountToman, dates[i].Minutes, totalMinutes);
            assigned += amounts[i];
        }

        var left = slice.AmountToman - assigned;
        var order = new int[dates.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (leftIndex, rightIndex) =>
        {
            var byFraction = fractions[rightIndex].CompareTo(fractions[leftIndex]);
            return byFraction != 0 ? byFraction : leftIndex.CompareTo(rightIndex);
        });
        var cursor = 0;
        while (left > 0 && order.Length > 0)
        {
            amounts[order[cursor % order.Length]]++;
            left--;
            cursor++;
        }

        var parts = new SlicePart[dates.Count];
        for (var i = 0; i < dates.Count; i++)
        {
            var start = i == 0
                ? slice.Start
                : TehranTime.At(dates[i].Date.Year, dates[i].Date.Month, dates[i].Date.Day, 0, 0);
            var end = i == dates.Count - 1 ? slice.End : TehranTime.At(dates[i + 1].Date.Year, dates[i + 1].Date.Month, dates[i + 1].Date.Day, 0, 0);
            parts[i] = new SlicePart(
                slice.SegmentId,
                slice.GroupId,
                start,
                end,
                dates[i].Minutes * 60L,
                dates[i].Minutes,
                amounts[i],
                slice.HourlyRateToman);
        }

        return parts;
    }

    private static IReadOnlyList<SlicePart> Divide(
        PricedSlice slice,
        IReadOnlyList<TimePackage> packages,
        Dictionary<string, long> remainingMinutes)
    {
        var marks = new SortedSet<DateTimeOffset> { slice.Start, slice.End };
        var splitsOnDate = false;
        foreach (var package in packages)
        {
            if (!remainingMinutes.TryGetValue(package.Id, out var left) || left <= 0)
            {
                continue;
            }

            if (package.ValidThrough is not null || package.AllowedFrom is not null)
            {
                splitsOnDate = true;
                break;
            }
        }

        if (splitsOnDate)
        {
            var midnight = TehranTime.NextMidnight(slice.Start);
            while (midnight < slice.End)
            {
                marks.Add(midnight);
                midnight = TehranTime.NextMidnight(midnight);
            }
        }

        var date = TehranTime.Date(slice.Start);
        foreach (var package in packages)
        {
            if (!remainingMinutes.TryGetValue(package.Id, out var left) || left <= 0)
            {
                continue;
            }

            if (package.AllowedFrom is TimeOfDay from)
            {
                AddMark(marks, slice, TehranTime.At(date, from));
            }

            if (package.AllowedUntil is TimeOfDay until)
            {
                AddMark(marks, slice, TehranTime.At(date, until));
            }
        }

        var points = marks.ToArray();
        if (points.Length == 2)
        {
            return [new SlicePart(slice.SegmentId, slice.GroupId, slice.Start, slice.End, slice.ActiveSeconds, slice.BilledMinutes, slice.AmountToman, slice.HourlyRateToman)];
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
            return [new SlicePart(slice.SegmentId, slice.GroupId, slice.Start, slice.End, slice.ActiveSeconds, slice.BilledMinutes, slice.AmountToman, slice.HourlyRateToman)];
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
                minutes = MoneyMath.MulDiv(slice.BilledMinutes, raw[i].Seconds, slice.ActiveSeconds);
                amount = MoneyMath.MulDiv(slice.AmountToman, raw[i].Seconds, slice.ActiveSeconds);
                usedMinutes += minutes;
                usedAmount += amount;
            }

            parts[i] = new SlicePart(slice.SegmentId, slice.GroupId, raw[i].Start, raw[i].End, raw[i].Seconds, minutes, amount, slice.HourlyRateToman);
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

            if (package.AllowedFrom is TimeOfDay from && package.AllowedUntil is TimeOfDay until && from == until)
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
        long AmountToman,
        long HourlyRateToman);

    private sealed class GraceTracker
    {
        public bool Enabled;
        public bool Used;
        public bool Stopped;
        public bool UncoveredShortfall;
        public long Waived;
        public DateTimeOffset At;
        public string? SegmentId;
    }

    /// <summary>
    /// Pays whole billing units the balance can afford at the session net price.
    /// A minute block grants grace for the shortfall and spends the leftover.
    /// A coarser block that does not fit grants that minute free and keeps the balance.
    /// Minutes played past that end are written off. The unplayed rest of a block is omitted.
    /// </summary>
    private static void ApplyMoney(
        ref long balance,
        long hourlyRate,
        long unpaidMinutes,
        long moneyDue,
        long activeSeconds,
        BillingPolicy billing,
        DiscountLedger discount,
        DateTimeOffset when,
        string? groupId,
        GraceTracker grace,
        DateTimeOffset at,
        string segmentId,
        ref long consumed,
        ref long uncovered,
        ref long writeOff,
        ref long omitted,
        ref long drawnGross)
    {
        if (moneyDue <= 0)
        {
            return;
        }

        long GrossPrefix(long minutes)
        {
            if (minutes <= 0)
            {
                return 0;
            }

            if (minutes >= unpaidMinutes)
            {
                return moneyDue;
            }

            var clean = MoneyMath.ChargeForMinutes(hourlyRate, unpaidMinutes, billing.Remainder) == moneyDue;
            return clean
                ? MoneyMath.ChargeForMinutes(hourlyRate, minutes, billing.Remainder)
                : MoneyMath.MulDiv(moneyDue, minutes, unpaidMinutes);
        }

        if (grace.Stopped || unpaidMinutes <= 0 || !grace.Enabled || grace.Used || hourlyRate <= 0)
        {
            var netDue = discount.Marginal(moneyDue, when, groupId);
            var pay = balance > 0 ? Math.Min(balance, netDue) : 0;
            balance -= pay;
            consumed += pay;
            var shortfall = netDue - pay;
            if (grace.UncoveredShortfall)
            {
                uncovered += shortfall;
            }
            else
            {
                writeOff += shortfall;
            }

            if (moneyDue > 0)
            {
                drawnGross += moneyDue;
                discount.Commit(moneyDue, when, groupId);
            }

            return;
        }

        var step = billing.GranularityMinutes;
        long taken = 0;
        while (taken + step <= unpaidMinutes)
        {
            var stepGross = GrossPrefix(taken + step) - GrossPrefix(taken);
            var netStep = discount.Marginal(stepGross, when, groupId);
            if (balance < netStep)
            {
                break;
            }

            balance -= netStep;
            consumed += netStep;
            drawnGross += stepGross;
            discount.Commit(stepGross, when, groupId);
            taken += step;
        }

        if (taken >= unpaidMinutes)
        {
            return;
        }

        if (step > 1 && balance > 0)
        {
            var minuteGross = GrossPrefix(taken + 1) - GrossPrefix(taken);
            if (minuteGross < 0)
            {
                minuteGross = 0;
            }

            var minuteNet = discount.Marginal(minuteGross, when, groupId);
            grace.Waived += minuteNet;
            grace.Used = true;
            grace.At = at;
            grace.SegmentId = segmentId;
            if (minuteGross > 0)
            {
                drawnGross += minuteGross;
                discount.Commit(minuteGross, when, groupId);
            }

            var covered = taken + 1;
            var restGross = moneyDue - GrossPrefix(covered);
            if (restGross < 0)
            {
                restGross = 0;
            }

            if (activeSeconds > covered * 60L && restGross > 0)
            {
                var extraNet = discount.Marginal(restGross, when, groupId);
                var pay = balance > 0 ? Math.Min(balance, extraNet) : 0;
                balance -= pay;
                consumed += pay;
                writeOff += extraNet - pay;
                drawnGross += restGross;
                discount.Commit(restGross, when, groupId);
            }
            else
            {
                omitted += restGross;
            }

            grace.Stopped = true;
            return;
        }

        if (balance > 0)
        {
            var minuteGross = GrossPrefix(taken + 1) - GrossPrefix(taken);
            var minuteNet = discount.Marginal(minuteGross, when, groupId);
            if (minuteNet < 0)
            {
                minuteNet = 0;
            }

            if (balance < minuteNet)
            {
                var pay = balance;
                grace.Waived += minuteNet - pay;
                grace.Used = true;
                grace.At = at;
                grace.SegmentId = segmentId;
                consumed += pay;
                balance = 0;
                if (minuteGross > 0)
                {
                    drawnGross += minuteGross;
                    discount.Commit(minuteGross, when, groupId);
                }

                taken += 1;
            }
        }

        if (taken < unpaidMinutes)
        {
            var restGross = moneyDue - GrossPrefix(taken);
            if (restGross < 0)
            {
                restGross = 0;
            }

            if (activeSeconds > taken * 60L && restGross > 0)
            {
                writeOff += discount.Marginal(restGross, when, groupId);
                drawnGross += restGross;
                discount.Commit(restGross, when, groupId);
            }
            else
            {
                omitted += restGross;
            }

            grace.Stopped = true;
        }
    }
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
        BillingPolicy? policy = null,
        bool graceMinuteEnabled = true,
        SessionDiscount? sessionDiscount = null)
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

            if (package.ValidThrough is LocalDate until && TehranTime.Date(at) > until)
            {
                continue;
            }

            if (package.AllowedFrom is TimeOfDay from && package.AllowedUntil is TimeOfDay to)
            {
                if (!InWindow(TehranTime.TimeOfDay(at), from, to))
                {
                    continue;
                }
            }

            packageMinutes += package.RemainingMinutes;
        }

        var ledger = new DiscountLedger(sessionDiscount ?? SessionDiscount.Empty);
        var discountedRate = sessionDiscount is not null
            && (sessionDiscount.LevelPercent > 0 || sessionDiscount.BirthdayPercent > 0 || sessionDiscount.CodePercent > 0 || sessionDiscount.FixedToman > 0);
        var free = quote.HourlyRateToman == 0;
        long? moneyMinutes;
        if (free)
        {
            moneyMinutes = null;
        }
        else if (discountedRate && account.MoneyBalanceToman > 0)
        {
            var discounted = DiscountedMinutes(quote.HourlyRateToman, account.MoneyBalanceToman, policy, ledger, at, groupId);
            if (discounted < 0)
            {
                free = true;
                moneyMinutes = null;
            }
            else
            {
                moneyMinutes = discounted;
            }
        }
        else
        {
            moneyMinutes = account.MoneyBalanceToman <= 0
                ? 0
                : MoneyMath.MaxAffordableMinutes(quote.HourlyRateToman, account.MoneyBalanceToman, policy);
        }

        if (graceMinuteEnabled && !free && quote.HourlyRateToman > 0 && account.MoneyBalanceToman > 0 && moneyMinutes is long owned)
        {
            var netOwned = ledger.NetOf(MoneyMath.ChargeForMinutes(quote.HourlyRateToman, owned, policy.Remainder), at, groupId);
            var left = account.MoneyBalanceToman - netOwned;
            if (left > 0)
            {
                var nextMinute = ledger.NetOf(MoneyMath.ChargeForMinutes(quote.HourlyRateToman, owned + 1, policy.Remainder), at, groupId) - netOwned;
                var nextBlock = policy.GranularityMinutes <= 1
                    ? nextMinute
                    : ledger.NetOf(MoneyMath.ChargeForMinutes(quote.HourlyRateToman, owned + policy.GranularityMinutes, policy.Remainder), at, groupId) - netOwned;
                if (nextBlock > left)
                {
                    moneyMinutes = owned + 1;
                }
            }
        }

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

    private static long DiscountedMinutes(long hourly, long balance, BillingPolicy policy, DiscountLedger ledger, DateTimeOffset at, string groupId)
    {
        var step = policy.GranularityMinutes;
        var blockNet = ledger.NetOf(MoneyMath.ChargeForMinutes(hourly, step, policy.Remainder), at, groupId);
        if (blockNet <= 0)
        {
            return -1;
        }

        var maxBlocks = (balance / blockNet) + 2;
        var rateCap = long.MaxValue / hourly / step;
        if (maxBlocks > rateCap)
        {
            maxBlocks = rateCap;
        }

        long lo = 0;
        long hi = maxBlocks;
        long best = 0;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var gross = MoneyMath.ChargeForMinutes(hourly, mid * step, policy.Remainder);
            if (ledger.NetOf(gross, at, groupId) <= balance)
            {
                best = mid * step;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    private static bool InWindow(TimeOfDay time, TimeOfDay from, TimeOfDay until)
    {
        if (from < until)
        {
            return time >= from && time < until;
        }

        return time >= from || time < until;
    }
}

/// <summary>
/// Level, birthday, and time-scoped code discounts used when a branch applies them to PC time.
/// Percents are taken off the same gross, then a fixed code is removed. The result is never negative.
/// </summary>
public readonly record struct PcTimeDiscount(int LevelBirthdayPercent, int CodePercent, long FixedToman)
{
    public bool IsNone => LevelBirthdayPercent <= 0 && CodePercent <= 0 && FixedToman <= 0;

    public long Net(long gross)
    {
        if (gross <= 0)
        {
            return 0;
        }

        var off = MoneyMath.PercentOf(gross, LevelBirthdayPercent) + MoneyMath.PercentOf(gross, CodePercent);
        if (off > gross)
        {
            off = gross;
        }

        var after = gross - off;
        if (FixedToman > 0)
        {
            after -= FixedToman < after ? FixedToman : after;
        }

        return after;
    }
}
