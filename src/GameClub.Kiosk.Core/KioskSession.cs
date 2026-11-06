using GameClub.Billing;

namespace GameClub.Kiosk.Core;

public enum KioskPhase
{
    Locked = 0,
    Active = 1,
    Grace = 2,
}

public enum SessionEndReason
{
    None = 0,
    UserLogout = 1,
    CreditExhausted = 2,
}

public sealed record StationContext(string StationId, string GroupId, PlayMode Mode, int PlayerCount);

public sealed record KioskSession
{
    public KioskPhase Phase { get; init; }

    public string? SessionId { get; init; }

    public string? MemberName { get; init; }

    public int? MemberAge { get; init; }

    public CustomerAccount? AccountAtLogin { get; init; }

    public CustomerAccount? Account { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    public SessionEndReason EndReason { get; init; }

    public string? ActiveGameId { get; init; }

    public string? Message { get; init; }

    public long LoginRevision { get; init; }

    public bool IsOpen => Phase is KioskPhase.Active or KioskPhase.Grace;

    public static KioskSession Locked(string? message = null) => new() { Phase = KioskPhase.Locked, Message = message };
}

/// <summary>
/// Lock time from <see cref="PrepaidPlanner"/>. A free band is unlimited only until it ends;
/// the paid minutes after it come from a second planner call. Grace is the planner's one minute.
/// </summary>
public sealed record PlayCoverage(
    DateTimeOffset? LockAt,
    DateTimeOffset? FreeUntil,
    long? SecondsAfterFree,
    DateTimeOffset? GraceAt);

public sealed record KioskProjection
{
    public KioskPhase Phase { get; init; }

    public long SpendableToman { get; init; }

    public long? RemainingSeconds { get; init; }

    public bool Unlimited { get; init; }

    public bool InGrace { get; init; }

    public bool ShouldLock { get; init; }

    public string MoneyText { get; init; } = "";

    public string TimeText { get; init; } = "";

    public CustomerAccount? Account { get; init; }

    public Settlement? Settlement { get; init; }

    public SessionCharge? Charge { get; init; }

    public DateTimeOffset? BillUntil { get; init; }

    public SessionEndReason EndReason { get; init; }
}

public static class PlayEligibility
{
    public const string Insufficient = "موجودی برای شروع بازی کافی نیست.";
    public const string DebtBlocked = "بدهی کیف پول اجازه بازی روی این سیستم را نمی‌دهد.";

    public static bool Check(
        CustomerAccount account,
        StationContext station,
        DateTimeOffset at,
        RateCard rates,
        BillingPolicy? policy,
        SpecialDayCalendar? specialDays,
        out string? message)
    {
        policy ??= BillingPolicy.Default;
        specialDays ??= SpecialDayCalendar.Empty;
        var estimate = CustomerPlay.Estimate(
            account,
            station.GroupId,
            station.Mode,
            station.PlayerCount,
            at,
            rates,
            specialDays,
            policy);
        if (estimate.BlockedByDebt)
        {
            message = DebtBlocked;
            return false;
        }

        if (estimate.RateIsFree || estimate.TotalMinutes > 0)
        {
            message = null;
            return true;
        }

        message = Insufficient;
        return false;
    }
}

/// <summary>
/// Pure session clock. It prices with <see cref="SessionPricer"/> and settles with
/// <see cref="CustomerPlay"/>, so the PC pocket follows the billing engine: one grace
/// minute, then lock, and the balance is never pushed below zero.
/// </summary>
public static class KioskSessionMath
{
    public static KioskSession Begin(
        CustomerAccount account,
        string sessionId,
        string memberName,
        int? memberAge,
        long revision,
        DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new BillingException("invalid_session", "Session id is required.");
        }

        return new KioskSession
        {
            Phase = KioskPhase.Active,
            SessionId = sessionId,
            MemberName = memberName,
            MemberAge = memberAge,
            AccountAtLogin = account,
            Account = account,
            StartedAt = at,
            LoginRevision = revision,
        };
    }

    public static KioskProjection Project(
        KioskSession session,
        StationContext station,
        DateTimeOffset now,
        RateCard rates,
        BillingPolicy? policy = null,
        SpecialDayCalendar? specialDays = null)
    {
        policy ??= BillingPolicy.Default;
        specialDays ??= SpecialDayCalendar.Empty;
        if (!session.IsOpen || session.AccountAtLogin is null || session.StartedAt is null || session.SessionId is null)
        {
            return LockedView(session.Account, session.Message);
        }

        var started = session.StartedAt.Value;
        TehranTime.Ensure(started);
        TehranTime.Ensure(now);
        if (now < started)
        {
            now = started;
        }

        var coverage = Coverage(session.AccountAtLogin, station, started, rates, policy, specialDays);
        if (coverage.LockAt is not DateTimeOffset end)
        {
            var freeCharge = Price(session.SessionId, station, started, now, rates, policy, specialDays);
            var freePlay = CustomerPlay.Settle(freeCharge, session.AccountAtLogin);
            EnsurePcDidNotGoNegative(session.AccountAtLogin, freePlay.Account);
            return View(KioskPhase.Active, freePlay, freeCharge, now, null, true, false, false, SessionEndReason.None, null);
        }

        var inFree = coverage.FreeUntil is DateTimeOffset freeUntil && now < freeUntil;
        var billUntil = now < end ? now : end;
        var charge = Price(session.SessionId, station, started, billUntil, rates, policy, specialDays);
        var play = CustomerPlay.Settle(charge, session.AccountAtLogin);
        EnsurePcDidNotGoNegative(session.AccountAtLogin, play.Account);

        var remaining = now >= end ? 0 : SecondsBetween(now, end);
        var inGrace = !inFree
            && coverage.GraceAt is DateTimeOffset graceAt
            && now >= graceAt
            && remaining > 0;

        if (remaining <= 0)
        {
            return View(
                KioskPhase.Locked,
                play,
                charge,
                end,
                0,
                false,
                false,
                true,
                SessionEndReason.CreditExhausted,
                null);
        }

        var shown = inFree ? (long?)null : remaining;
        return View(
            inGrace ? KioskPhase.Grace : KioskPhase.Active,
            play,
            charge,
            billUntil,
            shown,
            false,
            inGrace,
            false,
            SessionEndReason.None,
            inFree ? coverage.FreeUntil : null,
            inFree ? coverage.SecondsAfterFree : null);
    }

    public static KioskSession Apply(KioskSession session, KioskProjection projection, DateTimeOffset now)
    {
        if (!session.IsOpen)
        {
            return session;
        }

        if (!projection.ShouldLock)
        {
            return session with
            {
                Phase = projection.Phase,
                Account = projection.Account,
            };
        }

        return session with
        {
            Phase = KioskPhase.Locked,
            Account = projection.Account,
            EndedAt = projection.BillUntil ?? now,
            EndReason = SessionEndReason.CreditExhausted,
            Message = "اعتبار تمام شد",
            ActiveGameId = null,
        };
    }

    public static KioskSession Logout(KioskSession session, KioskProjection projection, DateTimeOffset now)
    {
        var exhausted = projection.ShouldLock;
        return session with
        {
            Phase = KioskPhase.Locked,
            Account = projection.Account ?? session.Account,
            EndedAt = exhausted ? projection.BillUntil ?? now : now,
            EndReason = exhausted ? SessionEndReason.CreditExhausted : SessionEndReason.UserLogout,
            Message = exhausted ? "اعتبار تمام شد" : null,
            ActiveGameId = null,
        };
    }

    public static PlayCoverage Coverage(
        CustomerAccount account,
        StationContext station,
        DateTimeOffset startedAt,
        RateCard rates,
        BillingPolicy policy,
        SpecialDayCalendar specialDays)
    {
        startedAt = TehranTime.Ensure(startedAt);
        var estimate = CustomerPlay.Estimate(
            account,
            station.GroupId,
            station.Mode,
            station.PlayerCount,
            startedAt,
            rates,
            specialDays,
            policy);
        if (estimate.BlockedByDebt && estimate.EligiblePackageMinutes == 0 && !estimate.RateIsFree)
        {
            return new PlayCoverage(startedAt, null, null, null);
        }

        var spendable = account.PcMoneyToman > 0 ? account.PcMoneyToman : 0;
        PrepaidPlan? money = spendable > 0 || estimate.RateIsFree
            ? PrepaidPlanner.ForAmount(
                startedAt,
                spendable,
                "cover",
                station.StationId,
                station.GroupId,
                station.Mode,
                station.PlayerCount,
                rates,
                specialDays,
                policy,
                account.Settings.GraceMinuteEnabled)
            : null;

        if (money is { IsUnlimited: true } || estimate.RateIsFree)
        {
            var freeStart = money is { GrantedActiveSeconds: > 0 }
                ? TehranTime.AddSeconds(startedAt, money.GrantedActiveSeconds)
                : startedAt;
            var freeAt = FirstFreeInstant(freeStart, station, rates, specialDays);
            if (freeAt is not DateTimeOffset opens)
            {
                return new PlayCoverage(null, null, null, null);
            }

            var boundary = FreeBoundary(opens, station, rates, specialDays);
            if (boundary is not DateTimeOffset until)
            {
                return new PlayCoverage(null, null, null, null);
            }

            var consumed = money?.ConsumedToman ?? 0;
            var left = spendable - consumed;
            if (left < 0)
            {
                left = 0;
            }

            var tail = PrepaidPlanner.ForAmount(
                until,
                left,
                "after-free",
                station.StationId,
                station.GroupId,
                station.Mode,
                station.PlayerCount,
                rates,
                specialDays,
                policy,
                account.Settings.GraceMinuteEnabled);
            var after = tail.IsUnlimited ? 0 : tail.GrantedActiveSeconds;
            var lockAt = tail.IsUnlimited ? (DateTimeOffset?)null : TehranTime.AddSeconds(until, after);
            return new PlayCoverage(lockAt, until, after, tail.Grace?.At);
        }

        var packageSeconds = estimate.EligiblePackageMinutes * 60L;
        var moneySeconds = money?.GrantedActiveSeconds ?? 0;
        return new PlayCoverage(
            TehranTime.AddSeconds(startedAt, packageSeconds + moneySeconds),
            null,
            null,
            money?.Grace?.At);
    }

    private static DateTimeOffset? FirstFreeInstant(
        DateTimeOffset from,
        StationContext station,
        RateCard rates,
        SpecialDayCalendar specialDays)
    {
        if (RateResolver.Resolve(rates, specialDays, from, station.GroupId, station.Mode, station.PlayerCount).HourlyRateToman == 0)
        {
            return from;
        }

        var cursor = from;
        for (var step = 0; step < 8; step++)
        {
            var next = NextBoundary(cursor, rates, station.GroupId);
            if (next <= cursor)
            {
                return null;
            }

            if (RateResolver.Resolve(rates, specialDays, next, station.GroupId, station.Mode, station.PlayerCount).HourlyRateToman == 0)
            {
                return next;
            }

            cursor = next;
        }

        return null;
    }

    private static DateTimeOffset? FreeBoundary(
        DateTimeOffset at,
        StationContext station,
        RateCard rates,
        SpecialDayCalendar specialDays)
    {
        var quote = RateResolver.Resolve(rates, specialDays, at, station.GroupId, station.Mode, station.PlayerCount);
        if (quote.HourlyRateToman != 0)
        {
            return null;
        }

        if (quote.Source == RateSource.TimeBand && quote.BandId is string bandId)
        {
            foreach (var band in rates.Bands)
            {
                if (string.Equals(band.Id, bandId, StringComparison.Ordinal))
                {
                    return TehranTime.NextWallTime(at, band.End);
                }
            }
        }

        if (quote.Source == RateSource.SpecialDay)
        {
            return TehranTime.NextMidnight(at);
        }

        return null;
    }

    private static DateTimeOffset NextBoundary(DateTimeOffset cursor, RateCard card, string groupId)
    {
        var next = TehranTime.NextMidnight(cursor);
        foreach (var band in card.Bands)
        {
            if (!string.Equals(band.GroupId, groupId, StringComparison.Ordinal))
            {
                continue;
            }

            var start = TehranTime.NextWallTime(cursor, band.Start);
            var end = TehranTime.NextWallTime(cursor, band.End);
            if (start < next)
            {
                next = start;
            }

            if (end < next)
            {
                next = end;
            }
        }

        return next;
    }

    private static SessionCharge Price(
        string sessionId,
        StationContext station,
        DateTimeOffset start,
        DateTimeOffset end,
        RateCard rates,
        BillingPolicy policy,
        SpecialDayCalendar specialDays)
    {
        var session = new Session(
            sessionId,
            [new PlaySegment("play", start, end, station.StationId, station.GroupId, station.Mode, station.PlayerCount)]);
        return SessionPricer.Price(session, rates, specialDays, policy);
    }

    private static void EnsurePcDidNotGoNegative(CustomerAccount before, CustomerAccount after)
    {
        if (before.PcMoneyToman >= 0 && after.PcMoneyToman < 0)
        {
            throw new BillingException("invalid_money", "PC balance went negative.");
        }

        if (before.PcMoneyToman < 0 && after.PcMoneyToman < before.PcMoneyToman)
        {
            throw new BillingException("invalid_money", "PC play pushed an existing debt further negative.");
        }
    }

    private static long Spendable(CustomerAccount account) => account.PcMoneyToman > 0 ? account.PcMoneyToman : 0;

    private static long SecondsBetween(DateTimeOffset start, DateTimeOffset end) => TehranTime.WholeSeconds(start, end);

    private static KioskProjection View(
        KioskPhase phase,
        PcPlayResult play,
        SessionCharge charge,
        DateTimeOffset billUntil,
        long? remainingSeconds,
        bool unlimited,
        bool inGrace,
        bool shouldLock,
        SessionEndReason reason,
        DateTimeOffset? freeUntil,
        long? secondsAfterFree = null)
    {
        var spendable = Spendable(play.Account);
        var time = freeUntil is DateTimeOffset until && secondsAfterFree is long after
            ? PersianFormat.FreeUntil(until, after)
            : PersianFormat.Time(unlimited ? null : remainingSeconds, inGrace);
        return new KioskProjection
        {
            Phase = phase,
            SpendableToman = spendable,
            RemainingSeconds = unlimited || freeUntil is not null ? null : remainingSeconds,
            Unlimited = unlimited,
            InGrace = inGrace,
            ShouldLock = shouldLock,
            MoneyText = PersianFormat.Money(spendable),
            TimeText = time,
            Account = play.Account,
            Settlement = play.Settlement,
            Charge = charge,
            BillUntil = billUntil,
            EndReason = reason,
        };
    }

    private static KioskProjection LockedView(CustomerAccount? account, string? message)
    {
        var spendable = account is null ? 0 : Spendable(account);
        return new KioskProjection
        {
            Phase = KioskPhase.Locked,
            SpendableToman = spendable,
            RemainingSeconds = 0,
            MoneyText = PersianFormat.Money(spendable),
            TimeText = PersianFormat.Time(0, false),
            Account = account,
            ShouldLock = false,
            EndReason = SessionEndReason.None,
        };
    }
}
