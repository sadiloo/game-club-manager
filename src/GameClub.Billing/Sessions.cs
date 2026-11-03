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
/// Prices a session from one running active clock. Billed minutes are
/// <see cref="MoneyMath.BilledMinutes"/> of the total played time, so a pause, a controller
/// change, a band edge, or midnight does not round up on its own. Each billing block is
/// priced at the rate in force where that block starts, including a block that crosses
/// 08:30 or midnight. Contiguous same-rate pieces, including pieces the caller already
/// split, are charged once. A pause stays a gap: lines do not cross it.
/// </summary>
public static class SessionPricer
{
    public static SessionCharge Price(
        Session session,
        RateCard rates,
        SpecialDayCalendar? specialDays = null,
        BillingPolicy? policy = null)
    {
        specialDays ??= SpecialDayCalendar.Empty;
        policy ??= BillingPolicy.Default;
        policy.EnsureValid();
        Validate(session);

        var pauses = NormalizePauses(session.Pauses);
        var fragments = new List<Fragment>();
        long clock = 0;
        foreach (var segment in session.Segments.OrderBy(s => s.Start).ThenBy(s => s.Id, StringComparer.Ordinal))
        {
            var start = TehranTime.Ensure(segment.Start);
            var end = TehranTime.Ensure(segment.End);
            foreach (var piece in SubtractPauses(start, end, pauses))
            {
                var seconds = TehranTime.WholeSeconds(piece.Start, piece.End);
                if (seconds <= 0)
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
                    clock,
                    seconds));
                clock += seconds;
            }
        }

        var totalMinutes = MoneyMath.BilledMinutes(clock, policy);
        var blockMinutes = policy.GranularityMinutes;
        var blockSeconds = (long)blockMinutes * 60L;
        var blockCountLong = blockMinutes == 0 ? 0 : totalMinutes / blockMinutes;
        if (blockCountLong > int.MaxValue)
        {
            throw new BillingException(BillingErrors.InvalidTime, "The session is too long to price.");
        }

        var blockCount = (int)blockCountLong;
        var owners = new int[blockCount];
        var quotes = new RateQuote[blockCount];
        var walls = new DateTimeOffset[blockCount];
        for (var i = 0; i < blockCount; i++)
        {
            var at = (long)i * blockSeconds;
            var index = FragmentIndex(fragments, at);
            var fragment = fragments[index];
            var wall = TehranTime.AddSeconds(fragment.Start, at - fragment.Offset);
            owners[i] = index;
            walls[i] = wall;
            quotes[i] = RateResolver.Resolve(rates, specialDays, wall, fragment.GroupId, fragment.Mode, fragment.PlayerCount);
        }

        var drafts = new List<SliceDraft>();
        for (var f = 0; f < fragments.Count; f++)
        {
            AppendFragment(drafts, fragments[f], f, owners, quotes, walls, blockMinutes, rates, specialDays);
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

    internal static void Validate(Session session)
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
        int fragmentIndex,
        int[] owners,
        RateQuote[] quotes,
        DateTimeOffset[] walls,
        int blockMinutes,
        RateCard rates,
        SpecialDayCalendar specialDays)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < owners.Length; i++)
        {
            if (owners[i] != fragmentIndex)
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }

            last = i;
        }

        if (first < 0)
        {
            var quote = RateResolver.Resolve(rates, specialDays, fragment.Start, fragment.GroupId, fragment.Mode, fragment.PlayerCount);
            drafts.Add(SliceDraft.From(fragment, fragment.Start, fragment.End, fragment.Seconds, 0, quote));
            return;
        }

        var cursor = first;
        var lineStart = fragment.Start;
        while (cursor <= last)
        {
            var quote = quotes[cursor];
            var date = TehranTime.Date(walls[cursor]);
            var next = cursor + 1;
            while (next <= last
                && SameQuote(quotes[next], quote)
                && TehranTime.Date(walls[next]) == date)
            {
                next++;
            }

            var lineEnd = next <= last ? walls[next] : fragment.End;
            var seconds = TehranTime.WholeSeconds(lineStart, lineEnd);
            var minutes = (long)(next - cursor) * blockMinutes;
            if (seconds > 0 || minutes > 0)
            {
                drafts.Add(SliceDraft.From(fragment, lineStart, lineEnd, seconds, minutes, quote));
            }

            lineStart = lineEnd;
            cursor = next;
        }
    }

    private static int FragmentIndex(List<Fragment> fragments, long activeAt)
    {
        for (var i = 0; i < fragments.Count; i++)
        {
            var fragment = fragments[i];
            if (activeAt >= fragment.Offset && activeAt < fragment.Offset + fragment.Seconds)
            {
                return i;
            }
        }

        throw new BillingException(BillingErrors.InvalidTime, "A billed block fell outside the session.");
    }

    private static bool SameQuote(RateQuote left, RateQuote right)
    {
        return left.HourlyRateToman == right.HourlyRateToman
            && left.Source == right.Source
            && left.BandId == right.BandId;
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
            long offset,
            long seconds)
        {
            SegmentId = segmentId;
            StationId = stationId;
            GroupId = groupId;
            Mode = mode;
            PlayerCount = playerCount;
            Start = start;
            End = end;
            Offset = offset;
            Seconds = seconds;
        }

        public string SegmentId { get; }

        public string StationId { get; }

        public string GroupId { get; }

        public PlayMode Mode { get; }

        public int PlayerCount { get; }

        public DateTimeOffset Start { get; }

        public DateTimeOffset End { get; }

        public long Offset { get; }

        public long Seconds { get; }
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
        string? BandId)
    {
        public static SliceDraft From(Fragment fragment, DateTimeOffset start, DateTimeOffset end, long seconds, long minutes, RateQuote quote)
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
                quote.BandId);
        }

        public bool CanMerge(SliceDraft next)
        {
            return StationId == next.StationId
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
                BilledMinutes = BilledMinutes + next.BilledMinutes,
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
    IReadOnlyList<PricedSlice> Lines,
    GraceLine? Grace = null,
    bool IsUnlimited = false);

/// <summary>
/// Turns a duration cap or a money cap into an end time. Play is assumed continuous.
/// Amount conversion uses the same running-clock charge as <see cref="SessionPricer"/>.
/// A zero hourly rate is unlimited: the walk does not run out to a year.
/// The grace minute, when the leftover is below one minute, is exactly 60 seconds
/// priced with <see cref="MoneyMath.ChargeForMinutes"/> of one minute.
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
        if (opening.HourlyRateToman == 0)
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
        long previousCharge = -1;
        var becameFree = false;
        while (hi <= maxBlocks)
        {
            var charge = PriceMinutes(start, hi * step, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy).TotalAmountToman;
            if (charge == previousCharge)
            {
                becameFree = true;
                break;
            }

            if (charge > amountToman)
            {
                break;
            }

            affordableBlocks = hi;
            previousCharge = charge;
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
        var prefixEnd = TehranTime.AddSeconds(start, bestMinutes * 60L);
        var prefix = bestMinutes == 0
            ? new SessionCharge("prepaid", [], 0, 0, 0)
            : PriceSynthetic(start, prefixEnd, segmentId, stationId, groupId, mode, playerCount, rates, specialDays, policy);
        var consumed = prefix.TotalAmountToman;
        var unused = amountToman - consumed;
        if (becameFree)
        {
            return new PrepaidPlan(
                PrepaidKind.Amount,
                start,
                prefixEnd,
                prefix.TotalActiveSeconds,
                amountToman,
                consumed,
                unused,
                prefix.Lines,
                null,
                true);
        }

        GraceLine? grace = null;
        var lines = prefix.Lines.ToList();
        var end = prefixEnd;
        if (graceMinuteEnabled && unused > 0)
        {
            var quote = RateResolver.Resolve(rates, specialDays, end, groupId, mode, playerCount);
            if (quote.HourlyRateToman == 0)
            {
                return new PrepaidPlan(
                    PrepaidKind.Amount,
                    start,
                    end,
                    prefix.TotalActiveSeconds,
                    amountToman,
                    consumed,
                    unused,
                    lines,
                    null,
                    true);
            }

            var minuteCost = MoneyMath.ChargeForMinutes(quote.HourlyRateToman, 1, policy.Remainder);
            if (minuteCost > unused)
            {
                var waived = minuteCost - unused;
                var sliceEnd = TehranTime.AddSeconds(end, 60);
                lines.Add(new PricedSlice(
                    segmentId,
                    stationId,
                    groupId,
                    mode,
                    playerCount,
                    end,
                    sliceEnd,
                    60,
                    1,
                    quote.HourlyRateToman,
                    quote.Source,
                    quote.BandId,
                    minuteCost));
                grace = new GraceLine(1, waived, end, segmentId);
                end = sliceEnd;
                consumed = amountToman;
                unused = 0;
            }
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
            grace);
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
