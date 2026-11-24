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
        StationInternetStateService internetStates,
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
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "جلسه در حال به‌روزرسانی است.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");

            if (session.Status == SessionStatus.Closed)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "closed", "جلسه بسته شده است.");
            }

            if (expectedVersion is uint version && session.RowVersion != version)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_version", "جلسه تغییر کرده است. صفحه تازه شد؛ دوباره تلاش کنید.");
            }

            EnsureTransferEligible(session);

            var targetId = StationIdNormalizer.Normalize(rawTargetStationId);
            if (string.Equals(targetId, session.StationId, StringComparison.Ordinal))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "same_station", "نمی‌توان به همان ایستگاه منتقل کرد.");
            }

            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM stations WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    targetId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه مقصد در حال به‌روزرسانی است.");
            }

            var target = await db.Stations.Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == targetId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "ایستگاه مقصد یافت نشد.");
            var source = session.Station
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "ایستگاه جلسه مشخص نیست.");

            if (source.Group?.Kind != target.Group?.Kind)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "transfer_type", "جلسهٔ کنسول و کامپیوتر به هم منتقل نمی‌شوند.");
            }

            EnsureTargetStation(target);
            await EnsureTargetUnoccupiedAsync(db, target.Id, ct);

            var now = clock.UtcNow.UtcDateTime;
            await EnsureTargetTariffAsync(rates, session, target, now, ct);

            var sourceStationId = session.StationId;
            var openSegment = session.Segments.SingleOrDefault(s => s.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "invalid_session", "جلسه بخش بازی باز ندارد.");

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

            var nextKey = SegmentKeySupport.Allocate(session.Segments.Count);
            var clearedGame = session.CurrentGameTitleId;
            var transferMode = target.Group!.Kind == StationKind.Pc ? PlayMode.Online : session.Mode;
            var transferPlayers = target.Group.Kind == StationKind.Pc ? 1 : session.PlayerCount;
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = nextKey,
                StartUtc = now,
                StationId = target.Id,
                GroupId = target.GroupId,
                Mode = transferMode,
                PlayerCount = transferPlayers,
                Kind = target.Group.Kind,
                SplitReason = SplitReasonTransfer,
            });
            if (target.Group.Kind == StationKind.Pc)
            {
                session.Mode = PlayMode.Online;
            }

            session.CurrentGameTitleId = null;
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
                throw new ApiException(StatusCodes.Status409Conflict, ex.Code, BillingExceptionUserMessages.ForCode(ex.Code));
            }

            var transferSegment = session.Segments.Single(s => s.EndUtc is null);
            var snapshot = await LoadSnapshotAsync(rates, session, ct);
            if (target.Group!.Kind == StationKind.Pc)
            {
                var convertedBudget = DurationPrepaidConverter.TryConvertToAmountBudget(session, snapshot, "transfer", commandId);
                if (convertedBudget is not null)
                {
                    await audit.AppendAsync(
                        "prepaid.duration_converted",
                        new
                        {
                            sessionId,
                            session.PrepaidBookedActiveSeconds,
                            budgetToman = convertedBudget,
                            reason = "transfer",
                            commandId,
                        },
                        ct);
                }

                SessionPrepaidRecalculator.RecalcAmountPrepaid(session, transferSegment, snapshot, now);
            }
            else if (target.Group.Kind == StationKind.Console)
            {
                SessionPrepaidRecalculator.RecalcAmountPrepaid(session, transferSegment, snapshot, now);
            }

            if (source.Group?.Kind == StationKind.Pc)
            {
                await internetStates.BumpDesiredAsync(source.Id, true, now, null, "transfer_source", session.Id, ct);
                await internetStates.BumpDesiredAsync(target.Id, true, now, null, "session_start", session.Id, ct);
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
                    clearedGameTitleId = clearedGame,
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
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه مقصد از قبل جلسهٔ باز دارد.");
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
            throw new ApiException(StatusCodes.Status409Conflict, "not_transferable", "جلسه در این وضعیت قابل انتقال نیست.");
        }

        if (session.PendingBill is not null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "جلسه از قبل صورتحساب باز دارد.");
        }
    }

    private static void EnsureTargetStation(StationEntity target)
    {
        if (!target.IsActive)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "inactive", "ایستگاه مقصد غیرفعال است.");
        }

        if (target.OperationalState != StationOperationalState.Available)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "target_unavailable", "ایستگاه مقصد جلسه نمی‌پذیرد.");
        }

        if (target.Group is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "no_group", "ایستگاه مقصد گروه ندارد.");
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
            throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "ایستگاه مقصد از قبل جلسهٔ باز دارد.");
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
            var mode = target.Group!.Kind == StationKind.Pc ? PlayMode.Online : session.Mode;
            var players = target.Group.Kind == StationKind.Pc ? 1 : session.PlayerCount;
            _ = RateResolver.Resolve(
                snapshot.Card,
                SpecialDayCalendar.Empty,
                at,
                target.GroupId,
                mode,
                players);
        }
        catch (BillingException ex) when (ex.Code == BillingErrors.TariffNotFound)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "tariff_not_found", BillingExceptionUserMessages.ForCode(BillingErrors.TariffNotFound));
        }
    }

    private static Task<BranchRatesSnapshot> LoadSnapshotAsync(RateConfigService rates, PlaySessionEntity session, CancellationToken ct)
        => session.RateCardSnapshotId != 0
            ? rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : rates.LoadAsync(ct);
}
