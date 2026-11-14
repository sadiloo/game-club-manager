namespace GameClub.Billing;

public sealed record PlaySegment(
    string Id,
    DateTimeOffset Start,
    DateTimeOffset End,
    string StationId,
    string GroupId,
    PlayMode Mode,
    int PlayerCount,
    StationKind? Kind = null);

public sealed record PauseInterval(DateTimeOffset Start, DateTimeOffset End);

public sealed record Session(string Id, IReadOnlyList<PlaySegment> Segments, IReadOnlyList<PauseInterval>? Pauses = null);

/// <summary>Billed minutes of one priced line that start on a Tehran civil date. Daily reports use this; the line itself is not split.</summary>
public sealed record DateMinuteAllocation(LocalDate Date, long Minutes);

/// <summary>Contiguous billed minutes on the wall clock within one priced line.</summary>
public sealed record ActiveMinuteRun(DateTimeOffset Start, long Count);

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
    long AmountToman,
    IReadOnlyList<DateMinuteAllocation>? ByDate = null,
    IReadOnlyList<ActiveMinuteRun>? MinuteRuns = null);

public sealed record SessionCharge(
    string SessionId,
    IReadOnlyList<PricedSlice> Lines,
    long TotalActiveSeconds,
    long TotalBilledMinutes,
    long TotalAmountToman);

/// <summary>
/// Prices a session from one running active clock. Minutes are the ceiling of the total
/// played time, pauses excluded, and are never rounded half-up. Each minute is priced at
/// the rate where that minute starts. Consecutive minutes that share a rate, player count,
/// and band are one line, including across a pause, and that line's toman is rounded once.
/// </summary>
public static class SessionPricer
{
    public static SessionCharge Price(
        Session session,
        RateCard rates,
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null,
        StationDirectory? stations = null)
    {
        specialDays ??= SpecialDayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        policy.EnsureValid();
        Validate(session, stations);

        var pauses = NormalizePauses(session.Pauses);
        var fragments = new List<Fragment>();
        long clockTicks = 0;
        foreach (var segment in session.Segments.OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var start = TehranTime.Ensure(segment.Start);
            var end = TehranTime.Ensure(segment.End);
            foreach (var piece in SubtractPauses(start, end, pauses))
            {
                var durationTicks = TehranTime.DurationTicks(piece.Start, piece.End);
                if (durationTicks <= 0)
                {
                    continue;
                }

                fragments.Add(new Fragment(
                    segment.Id,
                    segment.StationId,
                    segment.GroupId,
                    segment.Mode,
                    segment.PlayerCount,
                    piece.Start,
                    piece.End,
                    clockTicks,
                    durationTicks));
                clockTicks += durationTicks;
            }
        }

        var clockSeconds = clockTicks / TimeSpan.TicksPerSecond;
        var totalMinutes = MoneyMath.BilledMinutes(clockSeconds, policy);
        var blockMinutes = policy.GranularityMinutes;
        var blockCount = totalMinutes / blockMinutes;
        var drafts = new List<SliceDraft>();
        foreach (var fragment in fragments)
        {
            AppendFragment(drafts, fragment, blockMinutes, blockCount, rates, specialDays);
        }

        var merged = Merge(drafts);
        var lines = new PricedSlice[merged.Count];
        long active = 0;
        long minutes = 0;
        long amount = 0;
        for (var i = 0; i < merged.Count; i++)
        {
            lines[i] = merged[i].Finalize(policy);
            active = MoneyMath.CheckedAdd(active, lines[i].ActiveSeconds);
            minutes = MoneyMath.CheckedAdd(minutes, lines[i].BilledMinutes);
            amount = MoneyMath.CheckedAdd(amount, lines[i].AmountToman);
        }

        return new SessionCharge(session.Id, lines, active, minutes, amount);
    }

    internal static long[]? BuildMinuteRunPrefix(IReadOnlyList<ActiveMinuteRun>? runs)
    {
        if (runs is null || runs.Count == 0)
        {
            return null;
        }

        var prefix = new long[runs.Count];
        long sum = 0;
        for (var i = 0; i < runs.Count; i++)
        {
            sum += runs[i].Count;
            prefix[i] = sum;
        }

        return prefix;
    }

    internal static DateTimeOffset BilledMinuteStart(
        IReadOnlyList<ActiveMinuteRun>? runs,
        DateTimeOffset lineStart,
        long index)
        => BilledMinuteStart(runs, null, lineStart, index);

    internal static DateTimeOffset BilledMinuteStart(
        IReadOnlyList<ActiveMinuteRun>? runs,
        long[]? runEndPrefix,
        DateTimeOffset lineStart,
        long index)
    {
        if (runs is null || runs.Count == 0)
        {
            return TehranTime.AddSeconds(lineStart, index * 60L);
        }

        if (runEndPrefix is not null && runEndPrefix.Length == runs.Count)
        {
            var lo = 0;
            var hi = runs.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (runEndPrefix[mid] <= index)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            var before = lo == 0 ? 0 : runEndPrefix[lo - 1];
            return TehranTime.AddSeconds(runs[lo].Start, (index - before) * 60L);
        }

        var left = index;
        foreach (var run in runs)
        {
            if (left < run.Count)
            {
                return TehranTime.AddSeconds(run.Start, left * 60L);
            }

            left -= run.Count;
        }

        return TehranTime.AddSeconds(lineStart, index * 60L);
    }

    internal static void Validate(Session session, StationDirectory? stations = null)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new BillingException(BillingErrors.InvalidSession, "Session id is required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in session.Segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Id) || !seen.Add(segment.Id))
            {
                throw new BillingException(BillingErrors.InvalidSession, $"Segment id '{segment.Id}' is missing or duplicated.");
            }

            var start = TehranTime.Ensure(segment.Start);
            var end = TehranTime.Ensure(segment.End);
            if (end < start)
            {
                throw new BillingException(BillingErrors.InvalidSession, $"Segment '{segment.Id}' ends before it starts.");
            }

            if (string.IsNullOrWhiteSpace(segment.StationId))
            {
                throw new BillingException(BillingErrors.InvalidSession, $"Segment '{segment.Id}' has no station.");
            }

            _ = TariffKey.ForSegment(segment.GroupId, segment.Mode, segment.PlayerCount);
        }

        var ordered = session.Segments.OrderBy(s => s.Start).ThenBy(s => s.End).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                throw new BillingException(
                    BillingErrors.InvalidSession,
                    $"Segments '{ordered[i - 1].Id}' and '{ordered[i].Id}' overlap.");
            }
        }

        if (stations is not null)
        {
            foreach (var segment in session.Segments)
            {
                if (!stations.TryGetGroup(segment.StationId, out var listed))
                {
                    throw new BillingException(BillingErrors.InvalidSession, $"Unknown station '{segment.StationId}'.");
                }

                if (segment.Kind is StationKind stated && stated != listed.Kind)
                {
                    throw new BillingException(BillingErrors.InvalidSession, "Segment kind does not match the station directory.");
                }

                if (!string.Equals(segment.GroupId, listed.Id, StringComparison.Ordinal))
                {
                    throw new BillingException(
                        BillingErrors.InvalidSession,
                        "Segment group does not match the station directory.");
                }
            }
        }

        if (session.Pauses is null)
        {
            return;
        }

        foreach (var pause in session.Pauses)
        {
            var start = TehranTime.Ensure(pause.Start);
            var end = TehranTime.Ensure(pause.End);
            if (end < start)
            {
                throw new BillingException(BillingErrors.InvalidSession, "A pause ends before it starts.");
            }
        }

        if (session.Pauses.Count == 0)
        {
            return;
        }

        foreach (var pause in session.Pauses)
        {
            if (pause.End <= pause.Start)
            {
                continue;
            }

            foreach (var segment in session.Segments)
            {
                if (pause.End <= segment.Start || pause.Start >= segment.End)
                {
                    continue;
                }

                if (segment.Kind is StationKind stated && stations is not null)
                {
                    if (!stations.TryGetGroup(segment.StationId, out var listed))
                    {
                        throw new BillingException(BillingErrors.InvalidSession, $"Unknown station '{segment.StationId}'.");
                    }

                    if (stated != listed.Kind)
                    {
                        throw new BillingException(BillingErrors.InvalidSession, "Segment kind does not match the station directory.");
                    }
                }

                var kind = segment.Kind;
                if (kind is null && stations is not null)
                {
                    if (!stations.TryGetGroup(segment.StationId, out var group))
                    {
                        throw new BillingException(BillingErrors.InvalidSession, $"Unknown station '{segment.StationId}'.");
                    }

                    kind = group.Kind;
                }

                if (kind is null)
                {
                    throw new BillingException(BillingErrors.InvalidSession, "Station kind is required when a pause overlaps a segment.");
                }

                if (kind == StationKind.Pc)
                {
                    throw new BillingException(
                        BillingErrors.PcPause,
                        "A PC session cannot be paused. PC time stops only when the customer logs out.");
                }
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

    private static IReadOnlyList<PauseInterval>? NormalizePauses(IReadOnlyList<PauseInterval>? pauses)
    {
        if (pauses is null)
        {
            return null;
        }

        var normalized = new PauseInterval[pauses.Count];
        for (var i = 0; i < pauses.Count; i++)
        {
            normalized[i] = new PauseInterval(TehranTime.Ensure(pauses[i].Start), TehranTime.Ensure(pauses[i].End));
        }

        return normalized;
    }

    private static void AppendFragment(
        List<SliceDraft> drafts,
        Fragment fragment,
        int blockMinutes,
        long blockCount,
        RateCard rates,
        SpecialDayCalendar specialDays)
    {
        var blockSeconds = (long)blockMinutes * 60L;
        var blockTicks = blockSeconds * TimeSpan.TicksPerSecond;
        var offset = fragment.OffsetTicks;
        var endOffset = offset + fragment.DurationTicks;
        var activeSeconds = fragment.DurationTicks / TimeSpan.TicksPerSecond;
        var first = CeilDiv(offset, blockTicks);
        var last = endOffset <= 0 ? 0 : ((endOffset - 1) / blockTicks) + 1;
        if (last > blockCount)
        {
            last = blockCount;
        }
        if (first >= last)
        {
            FoldSeconds(drafts, fragment.End, activeSeconds);
            return;
        }

        var leadTicks = (first * blockTicks) - offset;
        if (leadTicks > 0)
        {
            var leadSeconds = leadTicks / TimeSpan.TicksPerSecond;
            FoldSeconds(drafts, fragment.Start.AddTicks(leadTicks), leadSeconds);
        }

        var cursor = first;
        while (cursor < last)
        {
            var startDeltaTicks = (cursor * blockTicks) - offset;
            var wall = fragment.Start.AddTicks(startDeltaTicks);
            var quote = RateResolver.Resolve(rates, specialDays, wall, fragment.GroupId, fragment.Mode, fragment.PlayerCount);
            var boundary = RateBoundaries.Next(wall, rates, fragment.GroupId);
            var boundaryTicks = TehranTime.DurationTicks(wall, boundary);
            long next;
            if (boundaryTicks <= 0)
            {
                next = cursor + 1;
            }
            else
            {
                next = CeilDiv(offset + startDeltaTicks + boundaryTicks, blockTicks);
                if (next <= cursor)
                {
                    next = cursor + 1;
                }
            }

            if (next > last)
            {
                next = last;
            }

            var minutes = (next - cursor) * blockMinutes;
            DateTimeOffset lineEnd;
            long seconds;
            long overlapTicks;
            if (next >= last)
            {
                lineEnd = fragment.End;
                overlapTicks = endOffset - (cursor * blockTicks);
                seconds = overlapTicks / TimeSpan.TicksPerSecond;
            }
            else
            {
                var endDeltaTicks = (next * blockTicks) - offset;
                lineEnd = fragment.Start.AddTicks(endDeltaTicks);
                overlapTicks = endDeltaTicks - startDeltaTicks;
                seconds = overlapTicks / TimeSpan.TicksPerSecond;
            }

            if (minutes > 0 && (next >= last ? overlapTicks > 0 : seconds > 0))
            {
                drafts.Add(SliceDraft.From(
                    fragment,
                    wall,
                    lineEnd,
                    seconds,
                    minutes,
                    quote,
                    [new DateMinuteAllocation(TehranTime.Date(wall), minutes)]));
            }
            else if (seconds > 0)
            {
                FoldSeconds(drafts, lineEnd, seconds);
            }

            cursor = next;
        }
    }

    private static long CeilDiv(long numerator, long denominator)
    {
        if (numerator <= 0)
        {
            return 0;
        }

        return ((numerator - 1) / denominator) + 1;
    }

    private static void FoldSeconds(List<SliceDraft> drafts, DateTimeOffset end, long seconds)
    {
        if (seconds <= 0 || drafts.Count == 0)
        {
            return;
        }

        var last = drafts[drafts.Count - 1];
        drafts[drafts.Count - 1] = last with
        {
            End = end > last.End ? end : last.End,
            ActiveSeconds = last.ActiveSeconds + seconds,
        };
    }

    private static List<SliceDraft> Merge(List<SliceDraft> drafts)
    {
        var merged = new List<SliceDraft>(drafts.Count);
        foreach (var draft in drafts)
        {
            var last = merged.Count - 1;
            if (last >= 0 && merged[last].CanMerge(draft))
            {
                merged[last] = merged[last].Merge(draft);
            }
            else
            {
                merged.Add(draft);
            }
        }

        return merged;
    }

    private sealed class Fragment
    {
        public Fragment(
            string segmentId,
            string stationId,
            string groupId,
            PlayMode mode,
            int playerCount,
            DateTimeOffset start,
            DateTimeOffset end,
            long offsetTicks,
            long durationTicks)
        {
            SegmentId = segmentId;
            StationId = stationId;
            GroupId = groupId;
            Mode = mode;
            PlayerCount = playerCount;
            Start = start;
            End = end;
            OffsetTicks = offsetTicks;
            DurationTicks = durationTicks;
        }

        public string SegmentId { get; }

        public string StationId { get; }

        public string GroupId { get; }

        public PlayMode Mode { get; }

        public int PlayerCount { get; }

        public DateTimeOffset Start { get; }

        public DateTimeOffset End { get; }

        public long OffsetTicks { get; }

        public long DurationTicks { get; }
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
        long BilledMinutes,
        long HourlyRateToman,
        RateSource Source,
        string? BandId,
        IReadOnlyList<DateMinuteAllocation> ByDate,
        IReadOnlyList<ActiveMinuteRun> MinuteRuns)
    {
        public static SliceDraft From(
            Fragment fragment,
            DateTimeOffset start,
            DateTimeOffset end,
            long seconds,
            long minutes,
            RateQuote quote,
            IReadOnlyList<DateMinuteAllocation> byDate)
        {
            return new SliceDraft(
                fragment.SegmentId,
                fragment.StationId,
                fragment.GroupId,
                fragment.Mode,
                fragment.PlayerCount,
                start,
                end,
                seconds,
                minutes,
                quote.HourlyRateToman,
                quote.Source,
                quote.BandId,
                byDate,
                [new ActiveMinuteRun(start, minutes)]);
        }

        public bool CanMerge(SliceDraft next)
        {
            // A pause is not a new line. Consecutive play at the same rate, player count,
            // and band is one line, even when the wall clock has a gap or crosses midnight.
            return GroupId == next.GroupId
                && Mode == next.Mode
                && PlayerCount == next.PlayerCount
                && HourlyRateToman == next.HourlyRateToman
                && Source == next.Source
                && BandId == next.BandId;
        }

        public SliceDraft Merge(SliceDraft next)
        {
            return this with
            {
                End = next.End,
                ActiveSeconds = ActiveSeconds + next.ActiveSeconds,
                BilledMinutes = BilledMinutes + next.BilledMinutes,
                ByDate = CombineDates(ByDate, next.ByDate),
                MinuteRuns = CombineRuns(MinuteRuns, next.MinuteRuns),
            };
        }

        public PricedSlice Finalize(BillingPolicy policy)
        {
            var amount = MoneyMath.ChargeForMinutes(HourlyRateToman, BilledMinutes, policy.Remainder);
            return new PricedSlice(
                SegmentId,
                StationId,
                GroupId,
                Mode,
                PlayerCount,
                Start,
                End,
                ActiveSeconds,
                BilledMinutes,
                HourlyRateToman,
                Source,
                BandId,
                amount,
                ByDate,
                MinuteRuns);
        }

        private static IReadOnlyList<ActiveMinuteRun> CombineRuns(
            IReadOnlyList<ActiveMinuteRun> left,
            IReadOnlyList<ActiveMinuteRun> right)
        {
            if (right.Count == 0)
            {
                return left;
            }

            if (left.Count == 0)
            {
                return right;
            }

            var merged = new List<ActiveMinuteRun>(left.Count + right.Count);
            merged.AddRange(left);
            merged.AddRange(right);
            return merged;
        }

        private static IReadOnlyList<DateMinuteAllocation> CombineDates(
            IReadOnlyList<DateMinuteAllocation> left,
            IReadOnlyList<DateMinuteAllocation> right)
        {
            if (right.Count == 0)
            {
                return left;
            }

            var merged = new List<DateMinuteAllocation>(left.Count + right.Count);
            merged.AddRange(left);
            foreach (var item in right)
            {
                var index = merged.FindIndex(existing => existing.Date == item.Date);
                if (index >= 0)
                {
                    merged[index] = new DateMinuteAllocation(item.Date, merged[index].Minutes + item.Minutes);
                }
                else
                {
                    merged.Add(item);
                }
            }

            return merged;
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
    IReadOnlyList<PricedSlice> Lines,
    GraceLine? Grace = null,
    bool IsUnlimited = false,
    DateTimeOffset? FreeUntil = null,
    long? PaidMinutesAfterFree = null);

/// <summary>
/// Turns a duration cap or a money cap into an end time. Play is assumed continuous.
/// Amount conversion uses the same running-clock charge as <see cref="SessionPricer"/>.
/// A zero hourly rate is unlimited only while that rate lasts. A free band returns
/// <see cref="PrepaidPlan.FreeUntil"/> and then the paid minutes the budget buys.
/// The grace minute is folded into the last line at its marginal price,
/// <c>Price(prefix + 1) − Price(prefix)</c>, not priced as its own minute.
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
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null)
    {
        start = TehranTime.Ensure(start);
        if (playTime < TimeSpan.Zero)
        {
            throw new BillingException(BillingErrors.InvalidTime, "Prepaid duration cannot be negative.");
        }

        if (playTime.Ticks % TimeSpan.TicksPerSecond != 0)
        {
            throw new BillingException(BillingErrors.InvalidTime, "Prepaid duration must be a whole number of seconds.");
        }

        var end = TehranTime.AddSeconds(start, playTime.Ticks / TimeSpan.TicksPerSecond);
        var charge = PriceSynthetic(start, end, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy);
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
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null,
        bool graceMinuteEnabled = true)
    {
        start = TehranTime.Ensure(start);
        if (amountToman < 0)
        {
            throw new BillingException(BillingErrors.InvalidMoney, "Prepaid amount cannot be negative.");
        }

        specialDays ??= SpecialDayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        policy.EnsureValid();
        _ = TariffKey.ForSegment(groupId, mode, playerCount);

        var opening = RateResolver.Resolve(rates, specialDays, start, groupId, mode, playerCount);
        if (opening.HourlyRateToman == 0 && FreeThroughHorizon(start, groupId, mode, playerCount, rates, specialDays))
        {
            return Unlimited(start, amountToman);
        }

        if (amountToman == 0)
        {
            return new PrepaidPlan(PrepaidKind.Amount, start, start, 0, 0, 0, 0, []);
        }

        var step = policy.GranularityMinutes;
        var maxBlocks = MaxDays * 24L * 60L / step;
        long affordableBlocks = 0;
        long hi = 1;
        while (hi <= maxBlocks)
        {
            var charge = PriceMinutes(start, hi * step, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
            if (charge > amountToman)
            {
                break;
            }

            affordableBlocks = hi;
            if (hi > maxBlocks / 2)
            {
                hi = maxBlocks;
                var atCap = PriceMinutes(start, hi * step, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
                if (atCap <= amountToman)
                {
                    affordableBlocks = hi;
                }

                break;
            }

            hi *= 2;
        }

        long lo = affordableBlocks;
        var searchHi = hi > maxBlocks ? maxBlocks : hi;
        while (lo < searchHi)
        {
            var mid = lo + ((searchHi - lo + 1) / 2);
            var charge = PriceMinutes(start, mid * step, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
            if (charge <= amountToman)
            {
                lo = mid;
            }
            else
            {
                searchHi = mid - 1;
            }
        }

        var bestMinutes = lo * step;
        var maxMinutes = maxBlocks * step;
        if (step == 1)
        {
            while (bestMinutes + 1 <= maxMinutes)
            {
                var extended = PriceMinutes(start, bestMinutes + 1, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
                var soFar = bestMinutes == 0
                    ? 0
                    : PriceMinutes(start, bestMinutes, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
                if (extended != soFar || extended > amountToman)
                {
                    break;
                }

                bestMinutes++;
            }
        }

        var prefixEnd = TehranTime.AddSeconds(start, bestMinutes * 60L);
        var prefix = bestMinutes == 0
            ? new SessionCharge("prepaid", [], 0, 0, 0)
            : PriceSynthetic(start, prefixEnd, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy);
        var consumed = prefix.TotalAmountToman;
        var unused = amountToman - consumed;
        IReadOnlyList<PricedSlice> lines = prefix.Lines;
        var end = prefixEnd;
        GraceLine? grace = null;
        if (graceMinuteEnabled && unused > 0 && bestMinutes < maxMinutes)
        {
            var minuteStart = prefixEnd;
            var quote = RateResolver.Resolve(rates, specialDays, minuteStart, groupId, mode, playerCount);
            var minuteLines = lines.ToList();
            long marginal;
            if (minuteLines.Count > 0 && SameRate(minuteLines[minuteLines.Count - 1], quote))
            {
                var last = minuteLines[minuteLines.Count - 1];
                var mergedMinutes = last.BilledMinutes + 1;
                var mergedAmount = MoneyMath.ChargeForMinutes(last.HourlyRateToman, mergedMinutes, policy.Remainder);
                marginal = mergedAmount - last.AmountToman;
                minuteLines[minuteLines.Count - 1] = last with
                {
                    End = TehranTime.AddSeconds(minuteStart, 60),
                    ActiveSeconds = last.ActiveSeconds + 60,
                    BilledMinutes = mergedMinutes,
                    AmountToman = mergedAmount,
                    ByDate = AppendMinute(last.ByDate, TehranTime.Date(minuteStart)),
                    MinuteRuns = AppendMinuteRun(last.MinuteRuns, minuteStart),
                };
            }
            else
            {
                marginal = MoneyMath.ChargeForMinutes(quote.HourlyRateToman, 1, policy.Remainder);
                var minuteEnd = TehranTime.AddSeconds(minuteStart, 60);
                minuteLines.Add(new PricedSlice(
                    segmentId,
                    stationId,
                    groupId,
                    mode,
                    playerCount,
                    minuteStart,
                    minuteEnd,
                    60,
                    1,
                    quote.HourlyRateToman,
                    quote.Source,
                    quote.BandId,
                    marginal,
                    [new DateMinuteAllocation(TehranTime.Date(minuteStart), 1)],
                    [new ActiveMinuteRun(minuteStart, 1)]));
            }

            if (step > 1 && marginal > 0)
            {
                // The next block does not fit. The grace minute is free and the leftover balance stays.
                grace = new GraceLine(1, marginal, minuteStart, segmentId);
                lines = minuteLines;
                end = TehranTime.AddSeconds(minuteStart, 60);
            }
            else if (marginal > unused)
            {
                if (step == 1)
                {
                    grace = new GraceLine(1, marginal, minuteStart, segmentId);
                }
                else
                {
                    grace = new GraceLine(1, marginal - unused, minuteStart, segmentId);
                    consumed = amountToman;
                    unused = 0;
                }

                lines = minuteLines;
                end = TehranTime.AddSeconds(minuteStart, 60);
            }
        }

        DateTimeOffset? freeUntil = null;
        long? paidAfterFree = null;
        if (lines.Count > 0 && lines[0].HourlyRateToman == 0)
        {
            var freeEnd = lines[0].End;
            long paid = 0;
            foreach (var line in lines)
            {
                if (line.HourlyRateToman == 0 && line.End >= freeEnd)
                {
                    freeEnd = line.End;
                }
                else
                {
                    paid += line.BilledMinutes;
                }
            }

            freeUntil = freeEnd;
            paidAfterFree = paid;
        }

        return new PrepaidPlan(
            PrepaidKind.Amount,
            start,
            end,
            TehranTime.WholeSeconds(start, end),
            amountToman,
            consumed,
            unused,
            lines,
            grace,
            false,
            freeUntil,
            paidAfterFree);
    }

    private static bool FreeThroughHorizon(
        DateTimeOffset start,
        string groupId,
        PlayMode mode,
        int playerCount,
        RateCard rates,
        SpecialDayCalendar specialDays)
    {
        var cursor = start;
        var horizon = TehranTime.AddSeconds(start, MaxDays * 24L * 60L * 60L);
        while (cursor < horizon)
        {
            var quote = RateResolver.Resolve(rates, specialDays, cursor, groupId, mode, playerCount);
            if (quote.HourlyRateToman > 0)
            {
                return false;
            }

            var next = RateBoundaries.Next(cursor, rates, groupId);
            if (next <= cursor || next > horizon)
            {
                return true;
            }

            cursor = next;
        }

        return true;
    }

    private static PrepaidPlan Unlimited(DateTimeOffset start, long amountToman)
    {
        return new PrepaidPlan(
            PrepaidKind.Amount,
            start,
            start,
            0,
            amountToman,
            0,
            amountToman,
            [],
            null,
            true);
    }

    private static bool SameRate(PricedSlice line, RateQuote quote)
    {
        return line.HourlyRateToman == quote.HourlyRateToman
            && line.Source == quote.Source
            && line.BandId == quote.BandId;
    }

    private static IReadOnlyList<ActiveMinuteRun> AppendMinuteRun(IReadOnlyList<ActiveMinuteRun>? runs, DateTimeOffset start)
    {
        var copy = runs is null ? new List<ActiveMinuteRun>() : new List<ActiveMinuteRun>(runs);
        if (copy.Count > 0)
        {
            var last = copy[copy.Count - 1];
            var nextStart = TehranTime.AddSeconds(last.Start, last.Count * 60L);
            if (nextStart == start)
            {
                copy[copy.Count - 1] = new ActiveMinuteRun(last.Start, last.Count + 1);
                return copy;
            }
        }

        copy.Add(new ActiveMinuteRun(start, 1));
        return copy;
    }

    private static IReadOnlyList<DateMinuteAllocation> AppendMinute(IReadOnlyList<DateMinuteAllocation>? dates, LocalDate date)
    {
        var copy = dates is null ? new List<DateMinuteAllocation>() : new List<DateMinuteAllocation>(dates);
        if (copy.Count > 0 && copy[copy.Count - 1].Date == date)
        {
            var last = copy[copy.Count - 1];
            copy[copy.Count - 1] = new DateMinuteAllocation(date, last.Minutes + 1);
        }
        else
        {
            copy.Add(new DateMinuteAllocation(date, 1));
        }

        return copy;
    }

    private static SessionCharge PriceMinutes(
        DateTimeOffset start,
        long minutes,
        string segmentId,
        string stationId,
        string groupId,
        PlayMode mode,
        int playerCount,
        RateCard rates,
        SpecialDayCalendar specialDays,
        BillingPolicy policy)
    {
        var end = TehranTime.AddSeconds(start, minutes * 60L);
        return PriceSynthetic(start, end, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy);
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
        SpecialDayCalendar? specialDays,
        BillingPolicy? policy)
    {
        var session = new Session(
            "prepaid",
            [new PlaySegment(segmentId, start, end, stationId, groupId, mode, playerCount)]);
        return SessionPricer.Price(session, rates, specialDays, policy);
    }
}
