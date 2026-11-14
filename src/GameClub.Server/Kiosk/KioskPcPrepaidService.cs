using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskPcPrepaidService(
    AuditService audit,
    RateConfigService rates)
{
    public async Task ApplyDurationAsync(PlaySessionEntity session, int minutes, CancellationToken ct)
    {
        if (session.PrepaidKind != PrepaidSessionKind.None)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "prepaid_exists", "Session already has prepaid booking.");
        }

        var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        var seg = session.Segments.OrderBy(s => s.StartUtc).First();
        var start = TimeMapping.ToTehran(session.StartedAtUtc);
        var plan = PrepaidPlanner.ForDuration(
            start,
            TimeSpan.FromMinutes(minutes),
            seg.SegmentKey,
            session.StationId,
            seg.GroupId,
            seg.Mode,
            seg.PlayerCount,
            snapshot.Card);
        session.PrepaidKind = PrepaidSessionKind.Duration;
        session.PrepaidBookedActiveSeconds = plan.GrantedActiveSeconds;
        session.PrepaidAmountBudgetToman = null;
        session.PrepaidEndAlertSent = false;
        await audit.AppendAsync("kiosk.prepaid.duration", new { sessionId = session.Id, minutes, plan.GrantedActiveSeconds }, ct);
    }

    public async Task ApplyAmountAsync(PlaySessionEntity session, long amountToman, CancellationToken ct)
    {
        if (amountToman <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Prepaid amount must be positive.");
        }

        if (session.PrepaidKind != PrepaidSessionKind.None)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "prepaid_exists", "Session already has prepaid booking.");
        }

        var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        var seg = session.Segments.OrderBy(s => s.StartUtc).First();
        var start = TimeMapping.ToTehran(session.StartedAtUtc);
        PrepaidPlan plan;
        try
        {
            plan = PrepaidPlanner.ForAmount(
                start,
                amountToman,
                seg.SegmentKey,
                session.StationId,
                seg.GroupId,
                seg.Mode,
                seg.PlayerCount,
                snapshot.Card);
        }
        catch (BillingException ex)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "billing_error", ex.Message);
        }

        var opening = RateResolver.Resolve(snapshot.Card, SpecialDayCalendar.Empty, start, seg.GroupId, seg.Mode, seg.PlayerCount);
        var policy = BillingPolicy.Default;
        var oneMinute = MoneyMath.ChargeForMinutes(opening.HourlyRateToman, policy.GranularityMinutes, policy.Remainder);
        if (amountToman < oneMinute)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "below_minimum", "Amount is below the tariff minimum play time.");
        }

        if (plan.GrantedActiveSeconds <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "below_minimum", "Amount is below the tariff minimum play time.");
        }

        session.PrepaidKind = PrepaidSessionKind.Amount;
        session.PrepaidBookedActiveSeconds = plan.GrantedActiveSeconds;
        session.PrepaidAmountBudgetToman = amountToman;
        session.PrepaidEndAlertSent = false;
        await audit.AppendAsync("kiosk.prepaid.amount", new { sessionId = session.Id, amountToman, plan.GrantedActiveSeconds }, ct);
    }
}
