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

public sealed record SessionTransferResult(
    Guid SessionId,
    string StationId,
    SessionStatus Status,
    uint RowVersion);

public static class SessionTransferSupport
{
    public const string TransferCommandType = "transfer";
    public const string SplitReasonTransfer = "انتقال";

    public static async Task<SessionTransferResult> TransferAsync(
        AppDbContext db,
        IClock clock,
        AuditService audit,
        SessionBilling billing,
        RateConfigService rates,
        IOperatorPanelNotifier? panelNotifier,
        Guid sessionId,
        string rawTargetStationId,
        bool markSourceBroken,
        uint? expectedVersion,
        Guid commandId,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var existing = await db.SessionCommands.AsNoTracking()
                .FirstOrDefaultAsync(c => c.SessionId == sessionId && c.CommandId == commandId, ct);
            if (existing?.ResultJson is { } json)
            {
                return JsonSerializer.Deserialize<SessionTransferResult>(json)
                    ?? throw new InvalidOperationException("Corrupt transfer command result.");
            }

            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    sessionId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "Session is being updated.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");

            if (session.Status == SessionStatus.Closed)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
            }

            if (expectedVersion is uint version && session.RowVersion != version)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_version", "Session was updated; refresh the page.");
            }

            EnsureTransferEligible(session);

            var targetId = StationIdNormalizer.Normalize(rawTargetStationId);
            if (string.Equals(targetId, session.StationId, StringComparison.Ordinal))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "same_station", "Cannot transfer to the same station.");
            }

            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM stations WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    targetId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Target station is being updated.");
            }

            var target = await db.Stations.Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == targetId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Target station not found.");
            var source = session.Station
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "Session station is missing.");

            if (source.Group?.Kind != target.Group?.Kind)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "transfer_type", "Console and PC sessions cannot be transferred to each other.");
            }

            EnsureTargetStation(target);
            await EnsureTargetUnoccupiedAsync(db, target.Id, ct);

            var now = clock.UtcNow.UtcDateTime;
            await EnsureTargetTariffAsync(rates, session, target, now, ct);

            var sourceStationId = session.StationId;
            var openSegment = session.Segments.SingleOrDefault(s => s.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "Session has no open segment.");

            openSegment.EndUtc = now;

            if (session.Status == SessionStatus.Paused)
            {
                var openPause = session.Pauses.LastOrDefault(p => p.EndUtc is null);
                if (openPause is not null)
                {
                    openPause.EndUtc = now;
                }

                session.Status = SessionStatus.Open;
            }

            var nextKey = ((char)('a' + session.Segments.Count)).ToString();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = nextKey,
                StartUtc = now,
                StationId = target.Id,
                GroupId = target.GroupId,
                Mode = session.Mode,
                PlayerCount = session.PlayerCount,
                Kind = target.Group!.Kind,
                SplitReason = SplitReasonTransfer,
            });

            session.StationId = target.Id;
            if (target.Group.Kind == StationKind.Pc)
            {
                session.LastCheckpointUtc = now;
            }

            var openSale = await db.OpenSales.FirstOrDefaultAsync(o => o.SessionId == session.Id, ct);
            if (openSale is not null)
            {
                openSale.StationId = target.Id;
            }

            source.OperationalState = markSourceBroken
                ? StationOperationalState.Broken
                : StationOperationalState.Available;

            try
            {
                _ = billing.BuildCharge(session, now, await LoadSnapshotAsync(rates, session, ct));
            }
            catch (BillingException ex)
            {
                throw new ApiException(StatusCodes.Status409Conflict, ex.Code, ex.Message);
            }

            await audit.AppendAsync(
                "session.transfer",
                new
                {
                    sessionId,
                    fromStationId = sourceStationId,
                    toStationId = target.Id,
                    markSourceBroken,
                    commandId,
                },
                ct);

            await db.SaveChangesAsync(ct);

            var result = new SessionTransferResult(session.Id, session.StationId, session.Status, session.RowVersion);
            db.SessionCommands.Add(new SessionCommandEntity
            {
                SessionId = sessionId,
                CommandId = commandId,
                CommandType = TransferCommandType,
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
                return JsonSerializer.Deserialize<SessionTransferResult>(replay.ResultJson!)
                    ?? throw new InvalidOperationException("Corrupt transfer command result.");
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsActiveSessionUniqueViolation(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Target station already has an open session.");
            }

            await tx.CommitAsync(ct);

            if (panelNotifier is not null)
            {
                await panelNotifier.NotifyGridRefreshAsync(ct);
            }

            return result;
        });
    }

    private static void EnsureTransferEligible(PlaySessionEntity session)
    {
        if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_transferable", "Session cannot be transferred in its current state.");
        }

        if (session.PendingBill is not null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "Session already has an open bill.");
        }
    }

    private static void EnsureTargetStation(StationEntity target)
    {
        if (!target.IsActive)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "inactive", "Target station is deactivated.");
        }

        if (target.OperationalState != StationOperationalState.Available)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "target_unavailable", "Target station cannot accept a session.");
        }

        if (target.Group is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "no_group", "Target station has no group.");
        }
    }

    private static async Task EnsureTargetUnoccupiedAsync(AppDbContext db, string stationId, CancellationToken ct)
    {
        if (await db.Sessions.AnyAsync(
                s => s.StationId == stationId
                    && (s.Status == SessionStatus.Open
                        || s.Status == SessionStatus.Paused
                        || s.Status == SessionStatus.BillOpen
                        || s.Status == SessionStatus.AwaitingPayment),
                ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Target station already has an open session.");
        }
    }

    private static async Task EnsureTargetTariffAsync(
        RateConfigService rates,
        PlaySessionEntity session,
        StationEntity target,
        DateTime now,
        CancellationToken ct)
    {
        var snapshot = await LoadSnapshotAsync(rates, session, ct);
        var at = TimeMapping.ToTehran(now);
        try
        {
            _ = RateResolver.Resolve(
                snapshot.Card,
                SpecialDayCalendar.Empty,
                at,
                target.GroupId,
                session.Mode,
                session.PlayerCount);
        }
        catch (BillingException ex) when (ex.Code == BillingErrors.TariffNotFound)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "tariff_not_found", ex.Message);
        }
    }

    private static Task<BranchRatesSnapshot> LoadSnapshotAsync(RateConfigService rates, PlaySessionEntity session, CancellationToken ct)
        => session.RateCardSnapshotId != 0
            ? rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : rates.LoadAsync(ct);
}
