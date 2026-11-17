using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Time;

namespace GameClub.Server.Services;

public static class ConsolePrepaidRecalculator
{
    public static void RecalcAmountPrepaid(
        PlaySessionEntity session,
        PlaySegmentEntity openSegment,
        BranchRatesSnapshot snapshot,
        DateTime nowUtc)
    {
        if (session.PrepaidKind != PrepaidSessionKind.Amount
            || session.PrepaidAmountBudgetToman is not long budget)
        {
            return;
        }

        var now = TimeMapping.ToTehran(nowUtc);
        var billingSegments = new List<PlaySegment>();
        foreach (var seg in session.Segments.OrderBy(s => s.StartUtc))
        {
            var end = seg.EndUtc ?? nowUtc;
            billingSegments.Add(new PlaySegment(
                seg.SegmentKey,
                TimeMapping.ToTehran(seg.StartUtc),
                TimeMapping.ToTehran(end),
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
                    TimeMapping.ToTehran(p.EndUtc ?? nowUtc)))
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
}