using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Time;

namespace GameClub.Server.Services;

public static class SessionPrepaidRecalculator
{
    public static void RecalcAmountPrepaid(
        PlaySessionEntity session,
        PlaySegmentEntity openSegment,
        BranchRatesSnapshot snapshot,
        DateTime nowUtc,
        DateTime? billEndUtc = null)
    {
        if (session.PrepaidKind != PrepaidSessionKind.Amount
            || session.PrepaidAmountBudgetToman is not long budget)
        {
            return;
        }

        var endCap = billEndUtc ?? nowUtc;
        var now = TimeMapping.ToTehran(endCap);
        var billingSegments = new List<PlaySegment>();
        foreach (var seg in session.Segments.OrderBy(s => s.StartUtc))
        {
            var rawEnd = seg.EndUtc ?? endCap;
            var segEnd = TrimPcSegmentEnd(seg, rawEnd, endCap);
            var segStart = TrimPcSegmentStart(seg, segEnd, endCap);
            if (segEnd <= segStart)
            {
                continue;
            }

            billingSegments.Add(new PlaySegment(
                seg.SegmentKey,
                TimeMapping.ToTehran(segStart),
                TimeMapping.ToTehran(segEnd),
                seg.StationId,
                seg.GroupId,
                seg.Mode,
                seg.PlayerCount,
                seg.Kind,
                seg.GameTitleId,
                seg.GameHourlyRateToman,
                seg.GameNightHourlyRateToman,
                seg.SplitReason));
        }

        IReadOnlyList<PauseInterval>? pauses = null;
        if (session.Pauses.Count > 0)
        {
            pauses = session.Pauses
                .Select(p => new PauseInterval(
                    TimeMapping.ToTehran(p.StartUtc),
                    TimeMapping.ToTehran(p.EndUtc ?? endCap)))
                .ToList();
        }

        var continuation = new PlaySegment(
            openSegment.SegmentKey,
            now,
            now,
            openSegment.StationId,
            openSegment.GroupId,
            openSegment.Mode,
            openSegment.PlayerCount,
            openSegment.Kind,
            openSegment.GameTitleId,
            openSegment.GameHourlyRateToman,
            openSegment.GameNightHourlyRateToman,
            openSegment.SplitReason);

        var plan = PrepaidPlanner.ForAmountFromSegments(
            new Session(session.Id.ToString(), billingSegments, pauses),
            now,
            continuation,
            budget,
            snapshot.Card);

        var activeThroughNow = billingSegments.Count == 0
            ? 0
            : SessionPricer.Price(
                new Session(
                    session.Id.ToString(),
                    billingSegments.Select(s => s with { End = now <= s.End ? now : s.End }).ToList(),
                    pauses),
                snapshot.Card).TotalActiveSeconds;

        if (plan.GrantedActiveSeconds <= activeThroughNow)
        {
            plan = plan with { GrantedActiveSeconds = activeThroughNow + 60 };
        }

        session.PrepaidBookedActiveSeconds = plan.GrantedActiveSeconds;
        if (plan.GrantedActiveSeconds > activeThroughNow)
        {
            session.PrepaidEndAlertSent = false;
        }
    }

    internal static DateTime TrimPcSegmentStart(PlaySegmentEntity seg, DateTime segEnd, DateTime billEndUtc)
    {
        if (seg.Kind != StationKind.Pc)
        {
            return seg.StartUtc;
        }

        return seg.StartUtc < billEndUtc ? seg.StartUtc : billEndUtc;
    }

    internal static DateTime TrimPcSegmentEnd(PlaySegmentEntity seg, DateTime rawEnd, DateTime billEndUtc)
    {
        if (seg.Kind != StationKind.Pc)
        {
            return rawEnd;
        }

        return rawEnd < billEndUtc ? rawEnd : billEndUtc;
    }
}