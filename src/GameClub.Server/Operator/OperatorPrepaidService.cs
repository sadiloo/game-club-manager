using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorPrepaidService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    SessionBilling billing,
    RateConfigService rates)
{
    public async Task<object> StartDurationAsync(Guid sessionId, int minutes, CancellationToken ct)
    {
        var session = await LoadOpenConsoleSessionAsync(sessionId, ct);
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
        await audit.AppendAsync("prepaid.duration", new { sessionId, minutes, plan.GrantedActiveSeconds }, ct);
        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(sessionId, ct);
    }

    public async Task<object> StartAmountAsync(Guid sessionId, long amountToman, CancellationToken ct)
    {
        if (amountToman <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Prepaid amount must be positive.");
        }

        var session = await LoadOpenConsoleSessionAsync(sessionId, ct);
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

        var seg0 = session.Segments.OrderBy(s => s.StartUtc).First();
        var opening = RateResolver.Resolve(snapshot.Card, SpecialDayCalendar.Empty, start, seg0.GroupId, seg0.Mode, seg0.PlayerCount);
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
        await audit.AppendAsync("prepaid.amount", new { sessionId, amountToman, plan.GrantedActiveSeconds }, ct);
        await db.SaveChangesAsync(ct);
        return await GetStatusAsync(sessionId, ct);
    }

    public async Task<object> GetStatusAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Pauses)
            .Include(s => s.Segments)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        var active = await ActivePlaySecondsAsync(session, clock.UtcNow.UtcDateTime, ct);
        var booked = session.PrepaidBookedActiveSeconds ?? 0;
        var remaining = Math.Max(0, booked - active);
        var endAlert = session.PrepaidEndAlertSent || (booked > 0 && remaining == 0);
        return new
        {
            sessionId,
            prepaidKind = session.PrepaidKind.ToString(),
            bookedActiveSeconds = booked,
            remainingActiveSeconds = remaining,
            prepaidEndAlert = endAlert,
        };
    }

    public async Task MarkEndAlertSentAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        session.PrepaidEndAlertSent = true;
        await db.SaveChangesAsync(ct);
    }

    private async Task<PlaySessionEntity> LoadOpenConsoleSessionAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions
            .Include(s => s.Segments)
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        if (session.Station?.Group?.Kind != StationKind.Console)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "Prepaid applies to console/table sessions.");
        }

        if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "Session is not active.");
        }

        return session;
    }

    private async Task<long> ActivePlaySecondsAsync(PlaySessionEntity session, DateTime atUtc, CancellationToken ct)
    {
        var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        var charge = billing.BuildCharge(session, atUtc, snapshot);
        return charge.TotalActiveSeconds;
    }
}
