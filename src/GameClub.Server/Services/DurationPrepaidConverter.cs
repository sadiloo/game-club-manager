using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Time;

namespace GameClub.Server.Services;

public static class DurationPrepaidConverter
{
    public static long? TryConvertToAmountBudget(
        PlaySessionEntity session,
        BranchRatesSnapshot snapshot,
        string reason,
        Guid commandId)
    {
        if (session.PrepaidKind != PrepaidSessionKind.Duration
            || session.PrepaidBookedActiveSeconds is not long booked)
        {
            return null;
        }

        var first = session.Segments.OrderBy(s => s.StartUtc).First();
        var start = TimeMapping.ToTehran(session.StartedAtUtc);
        var budget = PrepaidPlanner.ForDuration(
            start,
            TimeSpan.FromSeconds(booked),
            first.SegmentKey,
            first.StationId,
            first.GroupId,
            first.Mode,
            first.PlayerCount,
            snapshot.Card).ConsumedToman;

        session.PrepaidKind = PrepaidSessionKind.Amount;
        session.PrepaidAmountBudgetToman = budget;
        session.PrepaidEndAlertSent = false;
        return budget;
    }
}