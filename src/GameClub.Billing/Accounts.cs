namespace GameClub.Billing;

public sealed record PcAccount(string Id, long MoneyBalanceToman);

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
    long MoneyConsumedToman,
    long UncoveredToman,
    PcAccount UpdatedAccount,
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
    /// line waives that minute once.
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
    private readonly Dictionary<LocalDate, bool>? _birthOnCache;

    public DiscountLedger(SessionDiscount spec)
    {
        _spec = spec;
        _fixedLeft = spec.FixedToman;
        if (spec.BirthdayPercent > 0 && spec.BirthDate is JalaliDate birth && !birth.IsEmpty)
        {
            _birthOnCache = new Dictionary<LocalDate, bool>();
        }
    }

    internal readonly record struct LedgerState(
        long All,
        long Code,
        long Birth,
        long Net,
        long PrevPercentTotal,
        long PercentCarry,
        long FixedLeft);

    public long All { get; private set; }

    public long CodeGross { get; private set; }

    public long BirthGross { get; private set; }

    private long _netAccum;

    private long _prevPercentTotal;

    private long _percentCarry;

    private long _fixedLeft;

    internal bool IsTrivial =>
        _spec.LevelPercent == 0
        && _spec.BirthdayPercent == 0
        && _spec.CodePercent == 0
        && _spec.FixedToman == 0;

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
        if (_spec.BirthdayPercent <= 0
            || _spec.BirthDate is not JalaliDate birth
            || birth.IsEmpty)
        {
            return false;
        }

        var date = TehranTime.Date(at);
        if (_birthOnCache is not null)
        {
            if (_birthOnCache.TryGetValue(date, out var cached))
            {
                return cached;
            }

            cached = (_spec.Birthday ?? BirthdayWindow.ExactDay).Includes(birth, date);
            _birthOnCache[date] = cached;
            return cached;
        }

        return (_spec.Birthday ?? BirthdayWindow.ExactDay).Includes(birth, date);
    }

    public long NetNow() => _netAccum;

    public LedgerState Snapshot()
        => new(All, CodeGross, BirthGross, _netAccum, _prevPercentTotal, _percentCarry, _fixedLeft);

    public long NetAt(LedgerState state) => state.Net;

    public LedgerState Advance(LedgerState state, long gross, DateTimeOffset at, string? groupId)
        => AdvanceState(state, gross, at, groupId);

    public long NetOf(long gross, DateTimeOffset at, string? groupId) => Marginal(gross, at, groupId);

    public long Marginal(long add, DateTimeOffset at, string? groupId)
    {
        if (add <= 0)
        {
            return 0;
        }

        if (IsTrivial)
        {
            return add;
        }

        var before = Snapshot();
        var after = AdvanceState(before, add, at, groupId);
        return after.Net - before.Net;
    }

    public void Commit(long add, DateTimeOffset at, string? groupId)
    {
        if (add <= 0)
        {
            return;
        }

        var next = AdvanceState(Snapshot(), add, at, groupId);
        All = next.All;
        CodeGross = next.Code;
        BirthGross = next.Birth;
        _netAccum = next.Net;
        _prevPercentTotal = next.PrevPercentTotal;
        _percentCarry = next.PercentCarry;
        _fixedLeft = next.FixedLeft;
    }

    private LedgerState AdvanceState(LedgerState state, long gross, DateTimeOffset at, string? groupId)
    {
        if (gross <= 0)
        {
            return state;
        }

        if (IsTrivial)
        {
            var codeOn = CodeOn(at, groupId);
            var birthOn = BirthOn(at);
            return new LedgerState(
                state.All + gross,
                state.Code + (codeOn ? gross : 0),
                state.Birth + (birthOn ? gross : 0),
                state.Net + gross,
                state.PrevPercentTotal,
                state.PercentCarry,
                state.FixedLeft);
        }

        var codeEligible = CodeOn(at, groupId);
        var birthEligible = BirthOn(at);
        var all = state.All + gross;
        var code = state.Code + (codeEligible ? gross : 0);
        var birth = state.Birth + (birthEligible ? gross : 0);
        var percentTotal = PercentTotal(all, code, birth);
        var share = percentTotal - state.PrevPercentTotal;
        var combinedRate = (birthEligible
            ? Math.Min(_spec.LevelPercent + _spec.BirthdayPercent, _spec.Cap)
            : Math.Min(_spec.LevelPercent, _spec.Cap))
            + (codeEligible ? _spec.CodePercent : 0);
        long percentDisc;
        if (combinedRate >= 100)
        {
            percentDisc = gross;
        }
        else
        {
            percentDisc = share;
            if (percentDisc > gross)
            {
                percentDisc = gross;
            }

            if (percentDisc < 0)
            {
                percentDisc = 0;
            }
        }

        var fixedLeft = state.FixedLeft;
        long fixedUse = 0;
        if (codeEligible && _spec.FixedToman > 0 && fixedLeft > 0)
        {
            var room = gross - percentDisc;
            if (room < 0)
            {
                room = 0;
            }

            fixedUse = fixedLeft < room ? fixedLeft : room;
            fixedLeft -= fixedUse;
        }

        var marginal = gross - percentDisc - fixedUse;
        if (marginal < 0)
        {
            marginal = 0;
        }

        if (marginal > gross)
        {
            marginal = gross;
        }

        return new LedgerState(all, code, birth, state.Net + marginal, percentTotal, 0, fixedLeft);
    }

    private long PercentTotal(long all, long codeG, long birthG)
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
                levelOff = MoneyMath.PercentOf(rest, Math.Min(_spec.LevelPercent, _spec.Cap)) + MoneyMath.PercentOf(birthG, _spec.LevelPercent);
                birthOff = MoneyMath.PercentOf(birthG, _spec.BirthdayPercent);
            }
            else
            {
                var combined = MoneyMath.PercentOf(birthG, _spec.Cap);
                var levelShare = sum == 0 ? 0 : MoneyMath.MulDiv(combined, _spec.LevelPercent, sum);
                levelOff = MoneyMath.PercentOf(rest, Math.Min(_spec.LevelPercent, _spec.Cap)) + levelShare;
                birthOff = combined - levelShare;
            }
        }
        else
        {
            levelOff = MoneyMath.PercentOf(all, Math.Min(_spec.LevelPercent, _spec.Cap));
            birthOff = 0;
        }

        var codeOff = MoneyMath.PercentOf(codeG, _spec.CodePercent);
        return levelOff + birthOff + codeOff;
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
        BillingPolicy? billing = null,
        bool graceMinuteEnabled = true,
        SessionDiscount? discount = null,
        bool shortfallIsUncovered = false)
    {
        billing ??= BillingPolicy.Default;
        billing.EnsureValid();
        Validate(account);

        long money = account.MoneyBalanceToman;
        if (money < 0)
        {
            return new Settlement(
                charge.TotalAmountToman,
                0,
                charge.TotalAmountToman,
                account,
                []);
        }

        long moneyConsumed = 0;
        long uncovered = 0;
        long writeOff = 0;
        long omitted = 0;
        var pricedDiscount = new DiscountLedger(discount ?? SessionDiscount.Empty);
        var grace = new GraceTracker { Enabled = graceMinuteEnabled && !shortfallIsUncovered, UncoveredShortfall = shortfallIsUncovered };
        var moneyDraws = new List<MoneyDraw>();

        var pricingNet = BuildSessionPricingNet(charge, billing, discount ?? SessionDiscount.Empty);

        long globalMinute = 0;
        foreach (var slice in charge.Lines)
        {
            var parts = Divide(slice);
            long minuteBase = 0;
            foreach (var part in parts)
            {
                ApplyPart(
                    part,
                    slice.MinuteRuns,
                    slice.Start,
                    minuteBase,
                    globalMinute,
                    pricingNet,
                    billing,
                    pricedDiscount,
                    grace,
                    ref money,
                    ref moneyConsumed,
                    ref uncovered,
                    ref writeOff,
                    ref omitted,
                    moneyDraws);
                minuteBase += part.BilledMinutes;
                globalMinute += part.BilledMinutes;
            }
        }

        if (grace.PerMinuteElseGrace && grace.PerMinuteElseGraceNet > 0 && money > 0)
        {
            var spend = Math.Min(money, grace.PerMinuteElseGraceNet);
            grace.Waived -= spend;
            moneyConsumed += spend;
            money -= spend;
        }

        OperatorAlert? alert = null;
        DailyReportEntry? report = null;
        var discountToman = pricedDiscount.All - pricedDiscount.NetNow();
        var settledParts = moneyConsumed + discountToman + grace.Waived + writeOff + omitted + uncovered;
        if (settledParts != charge.TotalAmountToman)
        {
            throw new BillingException(
                BillingErrors.InvalidMoney,
                "Settlement parts do not add up to the session gross.");
        }

        EnsureNonNegativeSettlement(moneyConsumed, discountToman, grace.Waived, writeOff, omitted, uncovered);

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
            moneyConsumed,
            uncovered,
            new PcAccount(account.Id, money),
            moneyDraws,
            grace.Waived > 0 ? new GraceLine(1, grace.Waived, grace.At, grace.SegmentId) : null,
            discountToman,
            writeOff,
            alert,
            report,
            omitted,
            pricedDiscount.All);
    }

    private static void EnsureNonNegativeSettlement(
        long moneyConsumed,
        long discountToman,
        long graceWaived,
        long writeOff,
        long omitted,
        long uncovered)
    {
        if (moneyConsumed < 0
            || discountToman < 0
            || graceWaived < 0
            || writeOff < 0
            || omitted < 0
            || uncovered < 0)
        {
            throw new BillingException(
                BillingErrors.InvalidMoney,
                "Settlement parts cannot be negative.");
        }
    }

    private static void ApplyPart(
        SlicePart part,
        IReadOnlyList<ActiveMinuteRun>? minuteRuns,
        DateTimeOffset lineStart,
        long minuteBase,
        long globalMinuteBase,
        long[] pricingNet,
        BillingPolicy billing,
        DiscountLedger discount,
        GraceTracker grace,
        ref long money,
        ref long moneyConsumed,
        ref long uncovered,
        ref long writeOff,
        ref long omitted,
        List<MoneyDraw> moneyDraws)
    {
        _ = globalMinuteBase;
        if (part.BilledMinutes == 0 && part.AmountToman == 0)
        {
            return;
        }

        if (part.BilledMinutes == 0)
        {
            var before = moneyConsumed;
            long gross = 0;
            ApplyMoney(ref money, part.HourlyRateToman, 0, part.AmountToman, part.ActiveSeconds, 0, minuteBase, minuteRuns, lineStart, billing, discount, part.GroupId, grace, part.Start, part.SegmentId, ref moneyConsumed, ref uncovered, ref writeOff, ref omitted, ref gross);
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

        long grossMoney = 0;
        ApplyMoney(ref money, part.HourlyRateToman, part.BilledMinutes, part.AmountToman, part.ActiveSeconds, 0, minuteBase, minuteRuns, lineStart, billing, discount, part.GroupId, grace, part.Start, part.SegmentId, ref moneyConsumed, ref uncovered, ref writeOff, ref omitted, ref grossMoney);
        if (grossMoney > 0)
        {
            moneyDraws.Add(new MoneyDraw(part.SegmentId, part.Start, part.BilledMinutes, grossMoney));
        }
    }

    private static long[] SeedSessionPricingNet(
        SessionCharge charge,
        BillingPolicy billing,
        DiscountLedger pricing)
    {
        if (pricing.IsTrivial)
        {
            return [];
        }

        var total = charge.TotalBilledMinutes;
        var cumulative = new long[total + 1];
        long global = 0;
        foreach (var slice in charge.Lines)
        {
            var parts = Divide(slice);
            long minuteBase = 0;
            foreach (var part in parts)
            {
                if (part.BilledMinutes > 0)
                {
                    AppendPricingNet(
                        pricing,
                        part,
                        slice.MinuteRuns,
                        slice.Start,
                        minuteBase,
                        part.BilledMinutes,
                        billing,
                        cumulative,
                        ref global);
                }

                minuteBase += part.BilledMinutes;
            }
        }

        return cumulative;
    }

    private static long[] BuildSessionPricingNet(
        SessionCharge charge,
        BillingPolicy billing,
        SessionDiscount spec)
    {
        if (spec.LevelPercent == 0
            && spec.BirthdayPercent == 0
            && spec.CodePercent == 0
            && spec.FixedToman == 0)
        {
            return [];
        }

        var scratch = new DiscountLedger(spec);
        return SeedSessionPricingNet(charge, billing, scratch);
    }

    private static void AppendPricingNet(
        DiscountLedger pricing,
        SlicePart part,
        IReadOnlyList<ActiveMinuteRun>? minuteRuns,
        DateTimeOffset lineStart,
        long minuteBase,
        long count,
        BillingPolicy billing,
        long[] cumulative,
        ref long global)
    {
        var runPrefix = SessionPricer.BuildMinuteRunPrefix(minuteRuns);
        var partRunCharge = MoneyMath.ChargeForMinutes(part.HourlyRateToman, part.BilledMinutes, billing.Remainder)
            - MoneyMath.ChargeForMinutes(part.HourlyRateToman, 0, billing.Remainder);
        var partClean = partRunCharge == part.AmountToman;
        for (long i = 0; i < count; i++)
        {
            long gross;
            if (partClean)
            {
                gross = MoneyMath.ChargeForMinutes(part.HourlyRateToman, i + 1, billing.Remainder)
                    - MoneyMath.ChargeForMinutes(part.HourlyRateToman, i, billing.Remainder);
            }
            else
            {
                var before = i == 0 ? 0 : MoneyMath.MulDiv(part.AmountToman, i, part.BilledMinutes);
                var after = i + 1 >= part.BilledMinutes
                    ? part.AmountToman
                    : MoneyMath.MulDiv(part.AmountToman, i + 1, part.BilledMinutes);
                gross = after - before;
            }

            var at = SessionPricer.BilledMinuteStart(minuteRuns, runPrefix, lineStart, minuteBase + i);
            pricing.Commit(gross, at, part.GroupId);
            global++;
            cumulative[global] = pricing.NetNow();
        }
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

    private static IReadOnlyList<SlicePart> Divide(PricedSlice slice)
        => [new SlicePart(slice.SegmentId, slice.GroupId, slice.Start, slice.End, slice.ActiveSeconds, slice.BilledMinutes, slice.AmountToman, slice.HourlyRateToman)];

    private static void Validate(PcAccount account)
    {
        if (string.IsNullOrWhiteSpace(account.Id))
        {
            throw new BillingException("invalid_account", "Account id is required.");
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
        public bool PerMinuteElseGrace;
        public long PerMinuteElseGraceNet;
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
        long playedBeforeMinutes,
        long minuteBase,
        IReadOnlyList<ActiveMinuteRun>? minuteRuns,
        DateTimeOffset lineStart,
        BillingPolicy billing,
        DiscountLedger discount,
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

        var remainder = billing.Remainder;
        var runCharge = MoneyMath.ChargeForMinutes(hourlyRate, minuteBase + unpaidMinutes, remainder)
            - MoneyMath.ChargeForMinutes(hourlyRate, minuteBase, remainder);
        var clean = unpaidMinutes > 0 && runCharge == moneyDue;

        long MinuteGross(long index)
        {
            if (clean)
            {
                return MoneyMath.ChargeForMinutes(hourlyRate, index + 1, remainder)
                    - MoneyMath.ChargeForMinutes(hourlyRate, index, remainder);
            }

            var local = index - minuteBase;
            if (local < 0)
            {
                return 0;
            }

            if (unpaidMinutes <= 0)
            {
                return moneyDue;
            }

            var before = local == 0 ? 0 : MoneyMath.MulDiv(moneyDue, local, unpaidMinutes);
            var after = local + 1 >= unpaidMinutes ? moneyDue : MoneyMath.MulDiv(moneyDue, local + 1, unpaidMinutes);
            return after - before;
        }

        var runPrefix = SessionPricer.BuildMinuteRunPrefix(minuteRuns);
        DateTimeOffset Instant(long index) => SessionPricer.BilledMinuteStart(minuteRuns, runPrefix, lineStart, index);

        long NetOf(long fromLocal, long count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (discount.IsTrivial)
            {
                if (clean && fromLocal == 0 && count == unpaidMinutes)
                {
                    return moneyDue;
                }

                if (clean)
                {
                    var fromIndex = minuteBase + fromLocal;
                    return MoneyMath.ChargeForMinutes(hourlyRate, fromIndex + count, remainder)
                        - MoneyMath.ChargeForMinutes(hourlyRate, fromIndex, remainder);
                }

                long sum = 0;
                for (long i = 0; i < count; i++)
                {
                    sum += MinuteGross(minuteBase + fromLocal + i);
                }

                return sum;
            }

            var state = discount.Snapshot();
            var startNet = discount.NetAt(state);
            for (long i = 0; i < count; i++)
            {
                var index = minuteBase + fromLocal + i;
                state = discount.Advance(state, MinuteGross(index), Instant(index), groupId);
            }

            var net = discount.NetAt(state) - startNet;
            return net;
        }

        long CommitRange(long fromLocal, long count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (discount.IsTrivial)
            {
                long added;
                if (clean)
                {
                    var fromIndex = minuteBase + fromLocal;
                    added = MoneyMath.ChargeForMinutes(hourlyRate, fromIndex + count, remainder)
                        - MoneyMath.ChargeForMinutes(hourlyRate, fromIndex, remainder);
                }
                else
                {
                    added = 0;
                    for (long i = 0; i < count; i++)
                    {
                        added += MinuteGross(minuteBase + fromLocal + i);
                    }
                }

                if (added > 0)
                {
                    discount.Commit(added, Instant(minuteBase + fromLocal), groupId);
                }

                return added;
            }

            long addedGross = 0;
            for (long i = 0; i < count; i++)
            {
                var index = minuteBase + fromLocal + i;
                var gross = MinuteGross(index);
                if (gross <= 0)
                {
                    continue;
                }

                addedGross += gross;
                discount.Commit(gross, Instant(index), groupId);
            }

            return addedGross;
        }

        if (unpaidMinutes <= 0 || !grace.Enabled || grace.Used || grace.Stopped || hourlyRate <= 0)
        {
            var netDue = unpaidMinutes > 0 ? NetOf(0, unpaidMinutes) : discount.Marginal(moneyDue, at, groupId);
            var pay = balance > 0 ? Math.Min(balance, netDue) : 0;
            balance -= pay;
            consumed += pay;
            var shortfall = netDue - pay;
            if (grace.UncoveredShortfall)
            {
                uncovered += shortfall;
            }
            else if (activeSeconds > 0)
            {
                writeOff += shortfall;
            }
            else
            {
                omitted += shortfall;
            }

            if (unpaidMinutes > 0)
            {
                drawnGross += CommitRange(0, unpaidMinutes);
            }
            else if (moneyDue > 0)
            {
                drawnGross += moneyDue;
                discount.Commit(moneyDue, at, groupId);
            }

            return;
        }

        var step = billing.GranularityMinutes;
        long taken = 0;
        var allNet = NetOf(0, unpaidMinutes);
        if (balance >= allNet)
        {
            balance -= allNet;
            consumed += allNet;
            drawnGross += CommitRange(0, unpaidMinutes);
            return;
        }

        if (step > 1)
        {
            var offset = minuteBase % step;
            if (offset > 0 && taken < unpaidMinutes)
            {
                var partial = step - offset;
                if (partial > unpaidMinutes - taken)
                {
                    partial = unpaidMinutes - taken;
                }

                var netPartial = NetOf(taken, partial);
                if (balance >= netPartial)
                {
                    balance -= netPartial;
                    consumed += netPartial;
                    drawnGross += CommitRange(taken, partial);
                    taken += partial;
                }
            }
        }

        while (taken + step <= unpaidMinutes)
        {
            var netStep = NetOf(taken, step);
            if (balance < netStep)
            {
                break;
            }

            balance -= netStep;
            consumed += netStep;
            drawnGross += CommitRange(taken, step);
            taken += step;
        }

        if (taken >= unpaidMinutes)
        {
            return;
        }

        var remainingCount = unpaidMinutes - taken;
        var remainingNet = NetOf(taken, remainingCount);
        if (balance >= remainingNet)
        {
            balance -= remainingNet;
            consumed += remainingNet;
            drawnGross += CommitRange(taken, remainingCount);
            return;
        }

        if (step > 1 && balance > 0)
        {
            var minuteNet = NetOf(taken, 1);
            var waived = minuteNet;
            grace.Waived += waived;
            grace.Used = true;
            grace.At = Instant(minuteBase + taken);
            grace.SegmentId = segmentId;
            drawnGross += CommitRange(taken, 1);
            var covered = taken + 1;
            var restCount = unpaidMinutes - covered;
            var restNet = NetOf(covered, restCount);
            if (activeSeconds > (playedBeforeMinutes + covered) * 60L && restCount > 0)
            {
                var pay = balance > 0 ? Math.Min(balance, restNet) : 0;
                balance -= pay;
                consumed += pay;
                writeOff += restNet - pay;
                drawnGross += CommitRange(covered, restCount);
            }
            else
            {
                long restGross = 0;
                for (long i = 0; i < restCount; i++)
                {
                    restGross += MinuteGross(minuteBase + covered + i);
                }

                omitted += restGross;
            }

            grace.Stopped = true;
            return;
        }

        if (balance > 0)
        {
            var minuteNet = NetOf(taken, 1);
            if (balance < minuteNet)
            {
                var graceEndsPlay = activeSeconds <= (playedBeforeMinutes + taken + 1) * 60L;
                if (step == 1 && graceEndsPlay)
                {
                    grace.Waived += minuteNet;
                    grace.Used = true;
                    grace.At = Instant(minuteBase + taken);
                    grace.SegmentId = segmentId;
                    drawnGross += CommitRange(taken, 1);
                    taken += 1;
                    grace.Stopped = true;
                }
                else
                {
                    grace.PerMinuteElseGrace = true;
                    grace.PerMinuteElseGraceNet = minuteNet;
                    grace.Waived += minuteNet;
                    grace.Used = true;
                    grace.At = Instant(minuteBase + taken);
                    grace.SegmentId = segmentId;
                    drawnGross += CommitRange(taken, 1);
                    taken += 1;
                }
            }
        }

        if (taken < unpaidMinutes)
        {
            var restCount = unpaidMinutes - taken;
            var restNet = NetOf(taken, restCount);
            if (activeSeconds > (playedBeforeMinutes + taken) * 60L && restCount > 0)
            {
                var pay = balance > 0 ? Math.Min(balance, restNet) : 0;
                balance -= pay;
                consumed += pay;
                writeOff += restNet - pay;
                drawnGross += CommitRange(taken, restCount);
            }
            else
            {
                long restGross = 0;
                for (long i = 0; i < restCount; i++)
                {
                    restGross += MinuteGross(minuteBase + taken + i);
                }

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
    /// Money is converted at the rate in force at <paramref name="at"/> only. A zero balance
    /// contributes no money minutes, so the client can log out when the estimate hits zero.
    /// A negative shared balance blocks PC play: <see cref="RemainingTimeEstimate.TotalMinutes"/>
    /// is zero until the debt is cleared. A zero hourly rate is free.
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
                account.MoneyBalanceToman,
                0,
                0,
                true);
        }

        var quote = RateResolver.Resolve(rates, specialDays, at, groupId, mode, playerCount);

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
                    moneyMinutes = owned == 0 ? 1 : owned + 1;
                }
            }
        }

        if (graceMinuteEnabled && !free && quote.HourlyRateToman > 0 && moneyMinutes is long shown && shown > 0)
        {
            var fundedOnly = MoneyMath.MaxAffordableMinutes(quote.HourlyRateToman, account.MoneyBalanceToman, policy);
            if (discountedRate)
            {
                var discountedMax = DiscountedMinutes(quote.HourlyRateToman, account.MoneyBalanceToman, policy, ledger, at, groupId);
                if (discountedMax >= 0)
                {
                    fundedOnly = discountedMax;
                }
            }

            if (shown == fundedOnly + 1 && fundedOnly > 0)
            {
                moneyMinutes = fundedOnly;
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
            account.MoneyBalanceToman,
            moneyMinutes,
            moneyMinutes ?? 0);
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
