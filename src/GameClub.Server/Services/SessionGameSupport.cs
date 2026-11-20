using System.Text.Json;
using GameClub.Billing;
using GameClub.Server;
using GameClub.Server.Data;
using GameClub.Server.Time;
using GameClub.Server.Data.Entities;
using GameClub.Server.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed record SessionGameChangeResult(
    Guid SessionId,
    int? CurrentGameTitleId,
    uint RowVersion);

public static class SessionGameSupport
{
    public const string GameCommandType = "game";

    public static async Task<SessionGameChangeResult> ChangeGameAsync(
        AppDbContext db,
        IClock clock,
        AuditService audit,
        SessionBilling billing,
        RateConfigService rates,
        GameTariffService gameTariff,
        IOperatorPanelNotifier? panelNotifier,
        Guid sessionId,
        int? gameTitleId,
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
                return JsonSerializer.Deserialize<SessionGameChangeResult>(json)
                    ?? throw new InvalidOperationException("Corrupt game command result.");
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

            if (session.Station?.Group?.Kind != StationKind.Console)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "تغییر بازی فقط برای ایستگاه کنسول مجاز است.");
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

            if (session.CurrentGameTitleId == gameTitleId)
            {
                return new SessionGameChangeResult(session.Id, session.CurrentGameTitleId, session.RowVersion);
            }

            var previousGame = session.CurrentGameTitleId;
            var previousPrepaid = session.PrepaidBookedActiveSeconds;
            long? previousHourly = null;
            long? previousNight = null;
            var openSegment = session.Segments.SingleOrDefault(s => s.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "جلسه بخش بازی باز ندارد.");

            if (gameTitleId is int pick)
            {
                var title = await gameTariff.EnsureGameForSessionAsync(pick, session.Mode, session.PlayerCount, ct);
                var (hourly, night) = await gameTariff.ResolveSnapshotsAsync(pick, session.Mode, session.PlayerCount, ct);
                previousHourly = openSegment.GameHourlyRateToman;
                previousNight = openSegment.GameNightHourlyRateToman;
                var now = clock.UtcNow.UtcDateTime;
                openSegment.EndUtc = now;
                var nextKey = ((char)('a' + session.Segments.Count)).ToString();
                var newSeg = new PlaySegmentEntity
                {
                    SegmentKey = nextKey,
                    StartUtc = now,
                    StationId = session.StationId,
                    GroupId = session.Station!.GroupId,
                    Mode = session.Mode,
                    PlayerCount = session.PlayerCount,
                    Kind = StationKind.Console,
                    SplitReason = GameTariffService.SplitReasonGameChange,
                };
                gameTariff.ApplySnapshots(newSeg, pick, hourly, night);
                newSeg.GameTitle = title;
                session.Segments.Add(newSeg);
                session.CurrentGameTitleId = pick;
            }
            else
            {
                var now = clock.UtcNow.UtcDateTime;
                openSegment.EndUtc = now;
                var nextKey = ((char)('a' + session.Segments.Count)).ToString();
                var newSeg = new PlaySegmentEntity
                {
                    SegmentKey = nextKey,
                    StartUtc = now,
                    StationId = session.StationId,
                    GroupId = session.Station!.GroupId,
                    Mode = session.Mode,
                    PlayerCount = session.PlayerCount,
                    Kind = StationKind.Console,
                    SplitReason = GameTariffService.SplitReasonGameChange,
                };
                gameTariff.ClearGame(newSeg);
                session.Segments.Add(newSeg);
                session.CurrentGameTitleId = null;
            }

            openSegment = session.Segments.Single(s => s.EndUtc is null);
            var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
            try
            {
                _ = billing.BuildCharge(session, clock.UtcNow.UtcDateTime, snapshot);
            }
            catch (BillingException ex)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ex.Code, BillingExceptionUserMessages.ForCode(ex.Code));
            }

            ConsolePrepaidRecalculator.RecalcAmountPrepaid(session, openSegment, snapshot, clock.UtcNow.UtcDateTime);

            await audit.AppendAsync(
                "session.game",
                new
                {
                    sessionId,
                    previousGameTitleId = previousGame,
                    gameTitleId,
                    previousHourly,
                    previousNight,
                    openSegment.GameHourlyRateToman,
                    openSegment.GameNightHourlyRateToman,
                    previousPrepaidBookedActiveSeconds = previousPrepaid,
                    prepaidBookedActiveSeconds = session.PrepaidBookedActiveSeconds,
                    commandId,
                },
                ct);

            await db.SaveChangesAsync(ct);
            var result = new SessionGameChangeResult(session.Id, session.CurrentGameTitleId, session.RowVersion);
            db.SessionCommands.Add(new SessionCommandEntity
            {
                SessionId = sessionId,
                CommandId = commandId,
                CommandType = GameCommandType,
                AppliedAtUtc = clock.UtcNow.UtcDateTime,
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
                return JsonSerializer.Deserialize<SessionGameChangeResult>(replay.ResultJson!)
                    ?? throw new InvalidOperationException("Corrupt game command result.");
            }

            await tx.CommitAsync(ct);
            if (panelNotifier is not null)
            {
                await panelNotifier.NotifyGridRefreshAsync(ct);
            }

            return result;
        });
    }
}