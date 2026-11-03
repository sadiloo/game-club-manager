namespace GameClub.Billing;

public sealed record PlaySegment(
    string Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    string StationId,
    string GroupId,
    PlayMode Mode,
    int PlayerCount);

public sealed record PauseInterval(DateTimeOffset Start, DateTimeOffset End);

public sealed record Session(string Id, IReadOnlyList<PlaySegment> Segments, IReadOnlyList<PauseInterval>? Pauses = null);

public sealed record PricedSlice(
    string SegmentId,
    string StationId,
    string GroupId,
    PlayMode Mode,
    int PlayerCount,
    DateTimeOffset Start,
    DateTimeOffset End,
    long ActiveSeconds,
    long BilledMinutes,
    long HourlyRateToman,
    RateSource Source,
    string? BandId,
    long AmountToman);

public sealed record SessionCharge(
    string SessionId,
    IReadOnlyList<PricedSlice> Lines,
    long TotalActiveSeconds,
    long TotalBilledMinutes,
    long TotalAmountToman);

/// <summary>
/// Prices a session. The caller splits play when the controller count, station, or mode changes.
/// The engine further splits each of those segments at midnight and at time-band edges, then
/// prices every piece at the rate in force at its start. Paused time is removed before that split.
/// Contiguous pieces of the same segment that resolve to the same rate on the same Tehran date
/// are merged before minute rounding, so a band edge that does not change the rate is not an
/// extra rounded minute. Same-rate pieces the caller already split stay separate.
/// </summary>
public static class SessionPricer
{
    public static SessionCharge Price(
        Session session,
        RateCard rates,
        HolidayCalendar? holidays = null,
        BillingPolicy? policy = null)
    {
        holidays ??= HolidayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        policy.EnsureValid();
        Validate(session);

        var drafts = new List<SliceDraft>();
        foreach (var segment in session.Segments.OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var (activeStart, activeEnd) in SubtractPauses(segment.Start, segment.End, session.Pauses))
            {
                var cursor = activeStart;
                while (cursor < activeEnd)
                {
                    var boundary = RateBoundaries.Next(cursor, rates);
                    var sliceEnd = boundary < activeEnd ? boundary : activeEnd;
                    var seconds = TehranTime.WholeSeconds(cursor, sliceEnd);
                    if (seconds > 0)
                    {
                        var quote = RateResolver.Resolve(rates, holidays, cursor, segment.GroupId, segment.Mode, segment.PlayerCount);
                        drafts.Add(new SliceDraft(
                            segment.Id,
                            segment.StationId,
                            segment.GroupId,
                            segment.Mode,
                            segment.PlayerCount,
                            cursor,
                            sliceEnd,
                            seconds,
                            quote.HourlyRateToman,
                            quote.Source,
                            quote.BandId));
                    }

                    cursor = sliceEnd;
                }
            }
        }

        var lines = Merge(drafts).Select(draft => draft.Finalize(policy)).ToArray();
        return new SessionCharge(
            session.Id,
            lines,
            lines.Sum(line => line.ActiveSeconds),
            lines.Sum(line => line.BilledMinutes),
            lines.Sum(line => line.AmountToman));
    }

    internal static void Validate(Session session)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new BillingException("invalid_session", "Session id is required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in session.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Id) || !seen.Add(segment.Id))
            {
                throw new BillingException("invalid_session", $"Segment id '{segment.Id}' is missing or duplicated.");
            }

            TehranTime.Ensure(segment.Start);
            TehranTime.Ensure(segment.End);
            if (segment.End < segment.Start)
            {
                throw new BillingException("invalid_session", $"Segment '{segment.Id}' ends before it starts.");
            }

            if (string.IsNullOrWhiteSpace(segment.StationId))
            {
                throw new BillingException("invalid_session", $"Segment '{segment.Id}' has no station.");
            }

            _ = TariffKey.ForSegment(segment.GroupId, segment.Mode, segment.PlayerCount);
        }

        var ordered = session.Segments.OrderBy(s => s.Start).ThenBy(s => s.End).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                throw new BillingException(
                    "invalid_session",
                    $"Segments '{ordered[i - 1].Id}' and '{ordered[i].Id}' overlap.");
            }
        }

        if (session.Pauses is null)
        {
            return;
        }

        foreach (var pause in session.Pauses)
        {
            TehranTime.Ensure(pause.Start);
            TehranTime.Ensure(pause.End);
            if (pause.End < pause.Start)
            {
                throw new BillingException("invalid_session", "A pause ends before it starts.");
            }
        }
    }

    internal static List<(DateTimeOffset Start, DateTimeOffset End)> SubtractPauses(
        DateTimeOffset start,
        DateTimeOffset end,
        IReadOnlyList<PauseInterval>? pauses)
    {
        var pieces = new List<(DateTimeOffset Start, DateTimeOffset End)> { (start, end) };
        if (pauses is null || pauses.Count == 0)
        {
            return pieces.Where(piece => piece.End > piece.Start).ToList();
        }

        foreach (var pause in pauses)
        {
            var next = new List<(DateTimeOffset Start, DateTimeOffset End)>();
            foreach (var piece in pieces)
            {
                if (pause.End <= piece.Start || pause.Start >= piece.End)
                {
                    next.Add(piece);
                    continue;
                }

                if (pause.Start > piece.Start)
                {
                    next.Add((piece.Start, pause.Start));
                }

                if (pause.End < piece.End)
                {
                    next.Add((pause.End, piece.End));
                }
            }

            pieces = next;
        }

        return pieces.Where(piece => piece.End > piece.Start).ToList();
    }

    private static List<SliceDraft> Merge(List<SliceDraft> drafts)
    {
        var merged = new List<SliceDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            if (merged.Count > 0 && merged[^1].CanMerge(draft))
            {
                merged[^1] = merged[^1].Merge(draft);
            }
            else
            {
                merged.Add(draft);
            }
        }

        return merged;
    }

    private sealed record SliceDraft(
        string SegmentId,
        string StationId,
        string GroupId,
        PlayMode Mode,
        int PlayerCount,
        DateTimeOffset Start,
        DateTimeOffset End,
        long ActiveSeconds,
        long HourlyRateToman,
        RateSource Source,
        string? BandId)
    {
        public bool CanMerge(SliceDraft next)
        {
            return SegmentId == next.SegmentId
                && StationId == next.StationId
                && GroupId == next.GroupId
                && Mode == next.Mode
                && PlayerCount == next.PlayerCount
                && HourlyRateToman == next.HourlyRateToman
                && Source == next.Source
                && BandId == next.BandId
                && End == next.Start
                && TehranTime.Date(Start) == TehranTime.Date(next.Start);
        }

        public SliceDraft Merge(SliceDraft next)
        {
            return this with
            {
                End = next.End,
                ActiveSeconds = ActiveSeconds + next.ActiveSeconds,
            };
        }

        public PricedSlice Finalize(BillingPolicy policy)
        {
            var minutes = MoneyMath.BilledMinutes(ActiveSeconds, policy);
            var amount = MoneyMath.ChargeForMinutes(HourlyRateToman, minutes, policy.Remainder);
            return new PricedSlice(
                SegmentId,
                StationId,
                GroupId,
                Mode,
                PlayerCount,
                Start,
                End,
                ActiveSeconds,
                minutes,
                HourlyRateToman,
                Source,
                BandId,
                amount);
        }
    }
}

public enum PrepaidKind
{
    Duration = 0,
    Amount = 1,
}

public sealed record PrepaidPlan(
    PrepaidKind Kind,
    DateTimeOffset Start,
    DateTimeOffset End,
    long GrantedActiveSeconds,
    long? AmountBudgetToman,
    long ConsumedToman,
    long? UnusedToman,
    IReadOnlyList<PricedSlice> Lines);

/// <summary>
/// Turns a duration cap or a money cap into an end time. Play is assumed continuous
/// (a later pause extends the wall clock but not the budget; re-run with the real
/// session once pauses exist). Amount conversion walks band and midnight boundaries
/// and never grants a block whose charge would exceed the remaining budget.
/// The walk stops after <see cref="MaxDays"/> if the budget is still not exhausted.
/// </summary>
public static class PrepaidPlanner
{
    public const int MaxDays = 366;

    public static PrepaidPlan ForDuration(
        DateTimeOffset start,
        TimeSpan playTime,
        string segmentId,
        string stationId,
        string groupId,
        PlayMode mode,
        int playerCount,
        RateCard rates,
        HolidayCalendar? holidays = null,
        BillingPolicy? policy = null)
    {
        TehranTime.Ensure(start);
        if (playTime < TimeSpan.Zero)
        {
            throw new BillingException("invalid_time", "Prepaid duration cannot be negative.");
        }

        if (playTime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new BillingException("invalid_time", "Prepaid duration must be a whole number of seconds.");
        }

        var end = TehranTime.AddSeconds(start, playTime.Ticks / TimeSpan.TicksPerSecond);
        var charge = PriceSynthetic(start, end, segmentId, stationId, groupId, mode, playerCount, rates, holidays, policy);
        return new PrepaidPlan(
            PrepaidKind.Duration,
            start,
            end,
            charge.TotalActiveSeconds,
            null,
            charge.TotalAmountToman,
            null,
            charge.Lines);
    }

    public static PrepaidPlan ForAmount(
        DateTimeOffset start,
        long amountToman,
        string segmentId,
        string stationId,
        string groupId,
        PlayMode mode,
        int playerCount,
        RateCard rates,
        HolidayCalendar? holidays = null,
        BillingPolicy? policy = null)
    {
        TehranTime.Ensure(start);
        if (amountToman < 0)
        {
            throw new BillingException("invalid_money", "Prepaid amount cannot be negative.");
        }

        holidays ??= HolidayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        policy.EnsureValid();
        _ = TariffKey.ForSegment(groupId, mode, playerCount);

        var cursor = start;
        var horizon = TehranTime.AddSeconds(start, MaxDays * 24L * 60L * 60L);
        long remaining = amountToman;
        var lines = new List<PricedSlice>();

        while (remaining > 0 && cursor < horizon)
        {
            var boundary = RateBoundaries.Next(cursor, rates);
            if (boundary > horizon)
            {
                boundary = horizon;
            }

            var spanSeconds = TehranTime.WholeSeconds(cursor, boundary);
            if (spanSeconds <= 0)
            {
                cursor = boundary;
                continue;
            }

            var quote = RateResolver.Resolve(rates, holidays, cursor, groupId, mode, playerCount);
            var take = AffordableSeconds(quote.HourlyRateToman, remaining, policy, spanSeconds);
            if (take <= 0)
            {
                break;
            }

            var sliceEnd = take == spanSeconds ? boundary : TehranTime.AddSeconds(cursor, take);
            var minutes = MoneyMath.BilledMinutes(take, policy);
            var charge = MoneyMath.ChargeForMinutes(quote.HourlyRateToman, minutes, policy.Remainder);
            if (charge > remaining)
            {
                throw new BillingException("invalid_money", "Prepaid grant produced a charge above the remaining budget.");
            }

            lines.Add(new PricedSlice(
                segmentId,
                stationId,
                groupId,
                mode,
                playerCount,
                cursor,
                sliceEnd,
                take,
                minutes,
                quote.HourlyRateToman,
                quote.Source,
                quote.BandId,
                charge));
            remaining -= charge;
            cursor = sliceEnd;
            if (sliceEnd < boundary)
            {
                break;
            }
        }

        return new PrepaidPlan(
            PrepaidKind.Amount,
            start,
            cursor,
            TehranTime.WholeSeconds(start, cursor),
            amountToman,
            amountToman - remaining,
            remaining,
            lines);
    }

    internal static long AffordableSeconds(long hourlyRate, long budget, BillingPolicy policy, long spanSeconds)
    {
        if (spanSeconds <= 0 || budget < 0)
        {
            return 0;
        }

        var fullMinutes = MoneyMath.BilledMinutes(spanSeconds, policy);
        var fullCharge = MoneyMath.ChargeForMinutes(hourlyRate, fullMinutes, policy.Remainder);
        if (fullCharge <= budget)
        {
            return spanSeconds;
        }

        var blockSeconds = (long)policy.GranularityMinutes * 60L;
        long lo = 0;
        long hi = spanSeconds / blockSeconds;
        long best = 0;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var seconds = mid * blockSeconds;
            var minutes = MoneyMath.BilledMinutes(seconds, policy);
            var charge = MoneyMath.ChargeForMinutes(hourlyRate, minutes, policy.Remainder);
            if (charge <= budget)
            {
                best = seconds;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    private static SessionCharge PriceSynthetic(
        DateTimeOffset start,
        DateTimeOffset end,
        string segmentId,
        string stationId,
        string groupId,
        PlayMode mode,
        int playerCount,
        RateCard rates,
        HolidayCalendar? holidays,
        BillingPolicy? policy)
    {
        var session = new Session(
            "prepaid",
            [new PlaySegment(segmentId, start, end, stationId, groupId, mode, playerCount)]);
        return SessionPricer.Price(session, rates, holidays, policy);
    }
}
