using System.Text.Json;
using GameClub.Billing;
using GameClub.Server;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Hubs;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed record SessionInternetChangeResult(
    Guid SessionId,
    bool InternetOn,
    uint RowVersion,
    long HourlyToman,
    long DesiredVersion,
    bool Pending,
    bool KioskConnected,
    string? NextBand);

public static class SessionInternetSupport
{
    public const string InternetCommandType = "internet";
    public const string SplitReasonInternet = "اینترنت";

    public static async Task<SessionInternetChangeResult> ChangeInternetAsync(
        AppDbContext db,
        IClock clock,
        AuditService audit,
        SessionBilling billing,
        RateConfigService rates,
        StationInternetStateService internetStates,
        IOperatorPanelNotifier? panelNotifier,
        Guid sessionId,
        bool on,
        uint? expectedVersion,
        Guid commandId,
        CancellationToken ct)
    {
        if (commandId == Guid.Empty)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "command_required", "شناسهٔ دستور الزامی است.");
        }

        if (expectedVersion is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "version_required", "نسخهٔ جلسه (expectedVersion) الزامی است.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var existing = await db.SessionCommands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.SessionId == sessionId && c.CommandId == commandId, ct);
            if (existing?.ResultJson is { } json)
            {
                return JsonSerializer.Deserialize<SessionInternetChangeResult>(json)
                    ?? throw new InvalidOperationException("Corrupt internet command result.");
            }

            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    sessionId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "جلسه در حال به‌روزرسانی است؛ دوباره تلاش کنید.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");

            if (session.Station?.Group?.Kind != StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "تغییر اینترنت فقط برای PC مجاز است.");
            }

            if (session.Status != SessionStatus.Open)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته است.");
            }

            if (expectedVersion is uint version && session.RowVersion != version)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_version", "جلسه به‌روز شده است؛ صفحه را تازه کنید.");
            }

            if (session.Status == SessionStatus.ManualSettle
                || session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "برای این جلسه قبلاً صورتحساب باز شده است.");
            }

            var targetMode = on ? PlayMode.Online : PlayMode.Offline;
            var currentlyOn = session.Mode == PlayMode.Online;
            var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
            if (currentlyOn == on)
            {
                return await BuildResultAsync(db, clock, session, internetStates, snapshot, ct);
            }
            var groupId = session.Station!.GroupId;
            if (!on && !PcRateCardSupport.HasOfflineRates(snapshot.Card, groupId))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "offline_rate_missing", "نرخ آفلاین این گروه وارد نشده است.");
            }

            var now = clock.UtcNow.UtcDateTime;
            var previousPrepaid = session.PrepaidBookedActiveSeconds;
            var openSegment = session.Segments.SingleOrDefault(s => s.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "جلسه بخش بازی باز ندارد.");

            var previousHourly = ResolveHourly(snapshot, session, now);
            openSegment.EndUtc = now;
            var nextKey = ((char)('a' + session.Segments.Count)).ToString();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = nextKey,
                StartUtc = now,
                StationId = session.StationId,
                GroupId = groupId,
                Mode = targetMode,
                PlayerCount = 1,
                Kind = StationKind.Pc,
                SplitReason = SplitReasonInternet,
            });
            session.Mode = targetMode;
            openSegment = session.Segments.Single(s => s.EndUtc is null);

            try
            {
                _ = billing.BuildCharge(session, now, snapshot);
            }
            catch (BillingException ex)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ex.Code, ex.Message);
            }

            var convertedBudget = DurationPrepaidConverter.TryConvertToAmountBudget(session, snapshot, "toggle", commandId);
            if (convertedBudget is not null)
            {
                await audit.AppendAsync(
                    "prepaid.duration_converted",
                    new
                    {
                        sessionId,
                        session.PrepaidBookedActiveSeconds,
                        budgetToman = convertedBudget,
                        reason = "toggle",
                        commandId,
                    },
                    ct);
            }

            SessionPrepaidRecalculator.RecalcAmountPrepaid(session, openSegment, snapshot, now);
            var newHourly = ResolveHourly(snapshot, session, now);
            var kioskConnected = await IsKioskConnectedAsync(db, session.StationId, clock, ct);
            var desiredVersion = await internetStates.BumpDesiredAsync(
                session.StationId,
                on,
                now,
                null,
                "toggle",
                session.Id,
                ct);

            await audit.AppendAsync(
                "session.internet",
                new
                {
                    sessionId,
                    stationId = session.StationId,
                    fromOn = currentlyOn,
                    toOn = on,
                    pressedAtUtc = now,
                    previousHourly,
                    newHourly,
                    newSegmentKey = openSegment.SegmentKey,
                    kioskConnected,
                    desiredVersion,
                    previousPrepaidBookedActiveSeconds = previousPrepaid,
                    prepaidBookedActiveSeconds = session.PrepaidBookedActiveSeconds,
                    commandId,
                },
                ct);

            await db.SaveChangesAsync(ct);
            var result = await BuildResultAsync(db, clock, session, internetStates, snapshot, ct);
            db.SessionCommands.Add(new SessionCommandEntity
            {
                SessionId = sessionId,
                CommandId = commandId,
                CommandType = InternetCommandType,
                AppliedAtUtc = now,
                ResultJson = JsonSerializer.Serialize(result),
            });

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsSessionCommandUniqueViolation(ex))
            {
                var replay = await db.SessionCommands.AsNoTracking()
                    .FirstAsync(c => c.SessionId == sessionId && c.CommandId == commandId, ct);
                return JsonSerializer.Deserialize<SessionInternetChangeResult>(replay.ResultJson!)
                    ?? throw new InvalidOperationException("Corrupt internet command result.");
            }

            await tx.CommitAsync(ct);
            if (panelNotifier is not null)
            {
                await panelNotifier.NotifyGridRefreshAsync(ct);
            }

            return result;
        });
    }

    private static long ResolveHourly(BranchRatesSnapshot snapshot, PlaySessionEntity session, DateTime nowUtc)
    {
        var at = TimeMapping.ToTehran(nowUtc);
        return RateResolver.Resolve(
            snapshot.Card,
            SpecialDayCalendar.Empty,
            at,
            session.Station!.GroupId,
            session.Mode,
            session.PlayerCount).HourlyRateToman;
    }

    private static async Task<bool> IsKioskConnectedAsync(AppDbContext db, string stationId, IClock clock, CancellationToken ct)
    {
        var device = await db.KioskDevices.AsNoTracking()
            .Where(d => d.StationId == stationId && !d.IsRevoked)
            .OrderByDescending(d => d.PairedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (device?.LastSeenUtc is null)
        {
            return false;
        }

        return device.LastSeenUtc >= clock.UtcNow.UtcDateTime.AddSeconds(-15);
    }

    private static async Task<SessionInternetChangeResult> BuildResultAsync(
        AppDbContext db,
        IClock clock,
        PlaySessionEntity session,
        StationInternetStateService internetStates,
        BranchRatesSnapshot snapshot,
        CancellationToken ct)
    {
        var row = await db.StationInternetStates.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StationId == session.StationId, ct);
        var nowUtc = clock.UtcNow.UtcDateTime;
        var hourly = ResolveHourly(snapshot, session, nowUtc);
        var nextTarget = session.Mode == PlayMode.Online ? PlayMode.Offline : PlayMode.Online;
        var (_, nextBand) = PcInternetRatePreview.Resolve(
            snapshot.Card,
            session.Station!.GroupId,
            nextTarget,
            nowUtc);
        return new SessionInternetChangeResult(
            session.Id,
            session.Mode == PlayMode.Online,
            session.RowVersion,
            hourly,
            row?.DesiredVersion ?? 0,
            row is not null && StationInternetStateService.IsPending(row),
            await IsKioskConnectedAsync(db, session.StationId, clock, ct),
            nextBand);
    }
}