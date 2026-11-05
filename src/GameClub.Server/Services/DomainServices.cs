using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Services;

public static class TimeMapping
{
    public static DateTimeOffset ToTehran(DateTime utc)
        => TehranTime.FromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static DateTime ToUtc(DateTimeOffset tehran) => TehranTime.Ensure(tehran).UtcDateTime;
}

public sealed class AuditService(AppDbContext db, IClock clock, Operator.OperatorContext operatorContext)
{
    public async Task AppendAsync(string action, object details, CancellationToken ct = default)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = clock.UtcNow.UtcDateTime,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
            ActorUserId = operatorContext.UserId,
        });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class StationService(AppDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<StationEntity>> ListAsync(CancellationToken ct)
        => await db.Stations.AsNoTracking().Include(s => s.Group).OrderBy(s => s.Id).ToListAsync(ct);

    public async Task<StationEntity> GetAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        return await db.Stations.AsNoTracking().Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
    }

    public async Task<StationEntity> RegisterAsync(string rawId, string name, string groupId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(rawId);
        if (string.IsNullOrEmpty(id))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_station", "Station id is required.");
        }

        var group = await db.StationGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_group", $"Unknown group '{groupId}'.");
        }

        if (await db.Stations.AnyAsync(s => s.Id == id, ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_station", $"Station '{id}' already exists.");
        }

        try
        {
            var station = new StationEntity { Id = id, Name = name, GroupId = groupId };
            db.Stations.Add(station);
            await audit.AppendAsync("station.register", new { stationId = id, groupId }, ct);
            return station;
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsStationIdUniqueViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_station", $"Station '{id}' already exists.");
        }
    }

    public async Task<StationEntity> UpdateAsync(string stationId, string? name, string? groupId, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var id = StationIdNormalizer.Normalize(stationId);
            await db.Database.ExecuteSqlRawAsync(
                """SELECT 1 FROM stations WHERE "Id" = {0} FOR UPDATE""",
                id);
            var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
            if (!station.IsActive)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "inactive", "Station is deactivated.");
            }

            if (name is not null)
            {
                station.Name = name;
            }

            if (groupId is not null)
            {
                _ = await db.StationGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new ApiException(StatusCodes.Status400BadRequest, "unknown_group", $"Unknown group '{groupId}'.");
            }

            if (groupId is not null && !string.Equals(groupId, station.GroupId, StringComparison.Ordinal))
            {
                if (await db.Sessions.AnyAsync(s => s.StationId == id && s.Status != SessionStatus.Closed, ct))
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Station has an active session; group cannot change.");
                }

                station.GroupId = groupId;
            }

            await audit.AppendAsync("station.update", new { stationId = id, name, groupId }, ct);
            await tx.CommitAsync(ct);
            return station;
        });
    }

    public async Task<StationEntity> DeactivateAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
        if (!station.IsActive)
        {
            return station;
        }

        station.IsActive = false;
        await audit.AppendAsync("station.deactivate", new { stationId = id }, ct);
        await db.SaveChangesAsync(ct);
        return station;
    }

    public async Task<StationEntity> UpdateGroupAsync(string stationId, string groupId, CancellationToken ct)
        => await UpdateAsync(stationId, null, groupId, ct);
}

public sealed class SessionBilling(AppDbContext db, RateConfigService rates)
{
    public async Task<SessionCharge> BuildChargeAsync(PlaySessionEntity session, DateTime endUtc, CancellationToken ct = default)
    {
        var snapshot = await LoadSnapshotForSessionAsync(session, ct);
        return BuildCharge(session, endUtc, snapshot);
    }

    private async Task<BranchRatesSnapshot> LoadSnapshotForSessionAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.RateCardSnapshotId != 0)
        {
            return await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        }

        return await rates.LoadAsync(ct);
    }

    public SessionCharge BuildCharge(PlaySessionEntity session, DateTime endUtc, BranchRatesSnapshot snapshot)
    {
        var directory = BuildDirectory();
        var segments = new List<PlaySegment>();
        foreach (var seg in session.Segments.OrderBy(s => s.StartUtc))
        {
            var end = seg.EndUtc ?? endUtc;
            segments.Add(new PlaySegment(
                seg.SegmentKey,
                TimeMapping.ToTehran(seg.StartUtc),
                TimeMapping.ToTehran(end),
                seg.StationId,
                seg.GroupId,
                seg.Mode,
                seg.PlayerCount,
                seg.Kind));
        }

        IReadOnlyList<PauseInterval>? pauses = null;
        if (session.Pauses.Count > 0)
        {
            pauses = session.Pauses
                .Select(p => new PauseInterval(
                    TimeMapping.ToTehran(p.StartUtc),
                    TimeMapping.ToTehran(p.EndUtc ?? endUtc)))
                .ToList();
        }

        var billingSession = new Session(session.Id.ToString(), segments, pauses);
        return SessionPricer.Price(billingSession, snapshot.Card, stations: directory);
    }

    private StationDirectory BuildDirectory()
    {
        var groups = db.StationGroups.AsNoTracking().AsEnumerable()
            .Select(g => new StationGroup(g.Id, g.Name, g.Kind));
        var stations = db.Stations.AsNoTracking().AsEnumerable()
            .Select(s => new Station(s.Id, s.Name, s.GroupId));
        return new StationDirectory(groups, stations);
    }
}

public sealed class SessionCloser(
    AppDbContext db,
    RateConfigService rates,
    SessionBilling billing,
    IClock clock)
{
    public async Task<InvoiceEntity> FinalizeFromPendingAsync(
        PlaySessionEntity session,
        PendingBillEntity bill,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        CancellationToken ct)
    {
        var number = await NextInvoiceNumberAsync(ct);
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            Number = number,
            SessionId = session.Id,
            UnroundedPayableToman = bill.UnroundedPayableToman,
            RoundingToman = bill.RoundingToman,
            CollectibleToman = bill.CollectibleToman,
            SurchargeToman = bill.SurchargeToman,
            BilledToUtc = bill.FrozenAtUtc,
            IssuedAtUtc = clock.UtcNow.UtcDateTime,
            Status = "confirmed",
            StopReason = bill.StopReason,
        };
        foreach (var pay in payments)
        {
            var method = Enum.Parse<PaymentMethod>(pay.Method, true);
            invoice.PaymentLines.Add(new PaymentLineEntity
            {
                Method = method,
                AmountToman = pay.AmountToman,
                DestinationCardId = pay.DestinationCardId,
                Reference = pay.Reference,
                ConfirmationStatus = method == PaymentMethod.CardToCard
                    ? PaymentConfirmationStatus.Pending
                    : PaymentConfirmationStatus.Settled,
            });
        }

        session.Status = SessionStatus.Closed;
        session.Invoice = invoice;
        db.Invoices.Add(invoice);
        db.PendingBills.Remove(bill);
        session.PendingBill = null;
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task<InvoiceEntity> CloseAsync(PlaySessionEntity session, DateTime endUtc, string auditReason, CancellationToken ct)
    {
        if (session.Status == SessionStatus.Closed)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
        }

        var snapshot = session.RateCardSnapshotId != 0
            ? await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : await rates.LoadAsync(ct);
        var charge = billing.BuildCharge(session, endUtc, snapshot);
        var timeLines = TimeChargeLine.FromSession(charge);
        var issuedAt = TimeMapping.ToTehran(endUtc);
        var invoiceCalc = InvoiceCalculator.Calculate(new InvoiceRequest(
            timeLines,
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            snapshot.Rounding,
            issuedAt));

        foreach (var seg in session.Segments.Where(s => s.EndUtc is null))
        {
            seg.EndUtc = endUtc;
        }

        foreach (var pause in session.Pauses.Where(p => p.EndUtc is null))
        {
            pause.EndUtc = endUtc;
        }

        session.EndedAtUtc = endUtc;
        session.Status = SessionStatus.Closed;

        var number = await NextInvoiceNumberAsync(ct);
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            Number = number,
            SessionId = session.Id,
            UnroundedPayableToman = invoiceCalc.NetBeforeRoundingToman,
            RoundingToman = invoiceCalc.RoundingToman,
            CollectibleToman = invoiceCalc.PayableToman,
            SurchargeToman = 0,
            BilledToUtc = endUtc,
            IssuedAtUtc = endUtc,
            Status = "confirmed",
        };
        session.Invoice = invoice;
        db.Invoices.Add(invoice);
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = clock.UtcNow.UtcDateTime,
            Action = "session.stop",
            DetailsJson = JsonSerializer.Serialize(new { sessionId = session.Id, reason = auditReason, invoiceNumber = number }),
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsInvoiceSessionUniqueViolation(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
        }

        return invoice;
    }

    private async Task<long> NextInvoiceNumberAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT nextval('invoice_number_seq')";
            var value = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}

public sealed class SessionService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    SessionBilling billing,
    SessionCloser closer,
    RateConfigService rates,
    OperatorBillService operatorBills)
{
    public async Task<PlaySessionEntity> StartAsync(string stationId, int playerCount, PlayMode mode, DateTimeOffset? startedAt, CancellationToken ct)
    {
        if (startedAt.HasValue)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "client_start_time",
                "Session start time is set by the server; do not send startedAt.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var normalizedId = StationIdNormalizer.Normalize(stationId);
            await db.Database.ExecuteSqlRawAsync(
                """SELECT 1 FROM stations WHERE "Id" = {0} FOR UPDATE""",
                normalizedId);
            var station = await db.Stations.Include(s => s.Group).FirstOrDefaultAsync(s => s.Id == normalizedId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
            if (station.Group is null || string.IsNullOrWhiteSpace(station.GroupId))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "no_group", "Station has no group.");
            }

            if (!station.IsActive)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "inactive", "Station is deactivated.");
            }

            if (await db.Sessions.AnyAsync(
                    s => s.StationId == station.Id
                        && (s.Status == SessionStatus.Open
                            || s.Status == SessionStatus.Paused
                            || s.Status == SessionStatus.BillOpen
                            || s.Status == SessionStatus.AwaitingPayment),
                    ct))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Station already has an open session.");
            }

            if (playerCount is < 1 or > 4)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_players", "Player count must be 1–4.");
            }

            var startUtc = clock.UtcNow.UtcDateTime;
            var rateSnapshot = await rates.CreateSnapshotEntityAsync(ct);

            var session = new PlaySessionEntity
            {
                Id = Guid.NewGuid(),
                StationId = station.Id,
                Status = SessionStatus.Open,
                StartedAtUtc = startUtc,
                Mode = mode,
                PlayerCount = playerCount,
                LastCheckpointUtc = station.Group.Kind == StationKind.Pc ? startUtc : null,
                RateCardSnapshot = rateSnapshot,
            };

            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = startUtc,
                StationId = station.Id,
                GroupId = station.GroupId,
                Mode = mode,
                PlayerCount = playerCount,
                Kind = station.Group.Kind,
            });

            db.Sessions.Add(session);
            try
            {
                await audit.AppendAsync("session.start", new { sessionId = session.Id, stationId = station.Id, playerCount, mode = mode.ToString() }, ct);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsActiveSessionUniqueViolation(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Station already has an open session.");
            }

            await tx.CommitAsync(ct);
            return session;
        });
    }

    public Task PauseAsync(Guid sessionId, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind == StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "pc_pause", "Pause is not allowed on a PC station.");
            }

            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
            }

            if (session.Status == SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "already_paused", "Session is already paused.");
            }

            var now = clock.UtcNow.UtcDateTime;
            session.Status = SessionStatus.Paused;
            session.Pauses.Add(new PauseIntervalEntity { StartUtc = now });
            try
            {
                await audit.AppendAsync("session.pause", new { sessionId }, innerCt);
            }
            catch (DbUpdateException ex) when (PostgresErrors.IsOpenPauseUniqueViolation(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "already_paused", "Session is already paused.");
            }
        }, ct);

    public Task ResumeAsync(Guid sessionId, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
            }

            if (session.Status != SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "Session is not paused.");
            }

            var openPause = session.Pauses.LastOrDefault(p => p.EndUtc is null)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "No open pause.");
            openPause.EndUtc = clock.UtcNow.UtcDateTime;
            session.Status = SessionStatus.Open;
            await audit.AppendAsync("session.resume", new { sessionId }, innerCt);
        }, ct);

    public Task<object> StopAsync(Guid sessionId, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
            }

            if (session.Status is SessionStatus.AwaitingPayment or SessionStatus.BillOpen)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "awaiting_payment", "Session already has an open bill.");
            }

            if (session.Station!.Group!.Kind == StationKind.Pc)
            {
                var endUtc = session.LastCheckpointUtc ?? session.StartedAtUtc;
                var bill = await operatorBills.CreatePcPendingBillAsync(session, endUtc, "pc_stop", "logout", innerCt);
                await audit.AppendAsync("session.stop", new { sessionId, reason = "stop", pendingBillId = bill.Id }, innerCt);
                return (object)new
                {
                    pending = true,
                    unroundedPayableToman = bill.UnroundedPayableToman,
                    roundingToman = bill.RoundingToman,
                    collectibleToman = bill.CollectibleToman,
                };
            }

            var invoice = await closer.CloseAsync(session, clock.UtcNow.UtcDateTime, "stop", innerCt);
            return (object)new
            {
                pending = false,
                invoice.Number,
                unroundedPayableToman = invoice.UnroundedPayableToman,
                roundingToman = invoice.RoundingToman,
                collectibleToman = invoice.CollectibleToman,
            };
        }, ct);

    public Task RecordCheckpointAsync(Guid sessionId, DateTimeOffset? at, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Pc)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "Checkpoints are for PC sessions only.");
            }

            if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
            {
                if (session.Status == SessionStatus.ManualSettle)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
                }

                throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
            }

            var serverNow = clock.UtcNow.UtcDateTime;
            var previous = session.LastCheckpointUtc ?? session.StartedAtUtc;
            DateTime? reportedUtc = null;

            if (at.HasValue)
            {
                reportedUtc = TimeMapping.ToUtc(TehranTime.Ensure(at.Value));
                if (reportedUtc > serverNow.AddSeconds(60))
                {
                    throw new ApiException(
                        StatusCodes.Status400BadRequest,
                        "checkpoint_in_future",
                        "Checkpoint time cannot be more than 60 seconds ahead of server time.");
                }

                if (reportedUtc < previous)
                {
                    session.Status = SessionStatus.ManualSettle;
                    session.ManualSettleReason = "rejected_checkpoint";
                    await audit.AppendAsync("session.manual_settle", new { sessionId, reason = "rejected_checkpoint" }, innerCt);
                    await db.SaveChangesAsync(innerCt);
                    return;
                }
            }

            DateTime acceptedUtc;
            if (reportedUtc is null)
            {
                acceptedUtc = serverNow;
            }
            else
            {
                var minReportServer = reportedUtc.Value <= serverNow ? reportedUtc.Value : serverNow;
                acceptedUtc = previous >= minReportServer ? previous : minReportServer;
            }

            session.LastCheckpointUtc = acceptedUtc;
            session.Checkpoints.Add(new CheckpointEntity { AtUtc = acceptedUtc });
            await audit.AppendAsync("session.checkpoint", new { sessionId, atUtc = acceptedUtc }, innerCt);
            await db.SaveChangesAsync(innerCt);
        }, ct);

    public Task ChangePlayerCountAsync(Guid sessionId, int playerCount, CancellationToken ct)
        => ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Console)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "Controller count changes apply to consoles only.");
            }

            if (session.Status == SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
            }

            if (playerCount is < 1 or > 4)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_players", "Player count must be 1–4.");
            }

            if (session.PlayerCount == playerCount)
            {
                return;
            }

            var now = clock.UtcNow.UtcDateTime;
            var openSegment = session.Segments.Single(s => s.EndUtc is null);
            openSegment.EndUtc = now;
            var nextKey = ((char)('a' + session.Segments.Count)).ToString();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = nextKey,
                StartUtc = now,
                StationId = session.StationId,
                GroupId = session.Station.GroupId,
                Mode = session.Mode,
                PlayerCount = playerCount,
                Kind = StationKind.Console,
            });
            session.PlayerCount = playerCount;
            await audit.AppendAsync("session.player_count", new { sessionId, playerCount }, innerCt);
        }, ct);

    public async Task<long> LiveAmountAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        return await LiveAmountAsync(session, ct);
    }

    public async Task<long> LiveAmountAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.Status == SessionStatus.ManualSettle)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
        }

        if (session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment)
        {
            var pending = session.PendingBill
                ?? await db.PendingBills.AsNoTracking().FirstOrDefaultAsync(b => b.SessionId == session.Id, ct);
            if (pending is not null)
            {
                return pending.CollectibleToman;
            }
        }

        var endUtc = clock.UtcNow.UtcDateTime;
        var charge = await billing.BuildChargeAsync(session, endUtc, ct);
        return charge.TotalAmountToman;
    }

    public async Task<PendingBillEntity> ResolveManualSettleAsync(Guid sessionId, DateTimeOffset? endAt, CancellationToken ct)
    {
        return await ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status != SessionStatus.ManualSettle)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "not_manual_settle", "Session is not awaiting manual settlement.");
            }

            var now = clock.UtcNow.UtcDateTime;
            var lastAccepted = session.LastCheckpointUtc ?? session.StartedAtUtc;
            DateTime endUtc;
            if (!endAt.HasValue)
            {
                endUtc = lastAccepted;
            }
            else
            {
                endUtc = TimeMapping.ToUtc(TehranTime.Ensure(endAt.Value));
                if (endUtc > now)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "future_end", "End time cannot be in the future.");
                }

                if (endUtc < lastAccepted)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "before_checkpoint", "End time must be at or after the last accepted checkpoint.");
                }
            }

            var pending = await operatorBills.CreatePcPendingBillAsync(session, endUtc, "manual_settle", session.ManualSettleReason, innerCt);
            await audit.AppendAsync("session.manual_settle_resolve", new { sessionId, endUtc }, innerCt);
            return pending;
        }, ct);
    }

    private async Task<PlaySessionEntity> LoadOpenSession(Guid id, CancellationToken ct)
    {
        var session = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        if (session.Status is SessionStatus.Closed)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
        }

        return session;
    }

    private async Task ExecuteWithSessionWriteLockAsync(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task> action,
        CancellationToken ct)
    {
        _ = await ExecuteWithSessionWriteLockAsync(sessionId, async (session, innerCt) =>
        {
            await action(session, innerCt);
            return false;
        }, ct);
    }

    private async Task LockSessionRowForUpdateAsync(Guid sessionId, CancellationToken ct)
    {
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
    }

    private async Task<T> ExecuteWithSessionWriteLockAsync<T>(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await LockSessionRowForUpdateAsync(sessionId, ct);

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

            var result = await action(session, ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }
}

public interface IDeadPcMonitor
{
    Task<int> RunDueClosuresAsync(CancellationToken ct);
}

public sealed class DeadPcMonitor(IServiceScopeFactory scopes, IClock clock, ILogger<DeadPcMonitor> logger) : IDeadPcMonitor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<int> RunDueClosuresAsync(CancellationToken ct)
    {
        var now = clock.UtcNow.UtcDateTime;
        List<(Guid Id, DateTime EndUtc, string StationId)> due;
        using (var listScope = scopes.CreateScope())
        {
            var listDb = listScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var candidates = await listDb.Sessions
                .AsNoTracking()
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Where(s => s.Status == SessionStatus.Open && s.LastCheckpointUtc != null)
                .ToListAsync(ct);
            due = candidates
                .Where(s => s.Station?.Group?.Kind == StationKind.Pc
                    && s.LastCheckpointUtc!.Value <= now - Timeout)
                .Select(s => (s.Id, s.LastCheckpointUtc!.Value, s.StationId))
                .ToList();
        }

        var closed = 0;
        foreach (var item in due)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var operatorBills = scope.ServiceProvider.GetRequiredService<OperatorBillService>();
                var audit = scope.ServiceProvider.GetRequiredService<AuditService>();
                var strategy = db.Database.CreateExecutionStrategy();
                var closedThis = false;
                await strategy.ExecuteAsync(async () =>
                {
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    try
                    {
                        await db.Database.ExecuteSqlRawAsync(
                            """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                            item.Id);
                    }
                    catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
                    {
                        return;
                    }
                    var session = await db.Sessions
                        .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                        .Include(s => s.Segments)
                        .Include(s => s.Pauses)
                        .FirstAsync(s => s.Id == item.Id, ct);
                    if (session.Status != SessionStatus.Open)
                    {
                        return;
                    }

                    var lastAccepted = session.LastCheckpointUtc ?? session.StartedAtUtc;
                    if (lastAccepted > now - Timeout)
                    {
                        return;
                    }

                    await operatorBills.CreatePcPendingBillAsync(session, lastAccepted, "auto_close", "auto-close", ct);
                    db.AuditLog.Add(new AuditLogEntry
                    {
                        AtUtc = clock.UtcNow.UtcDateTime,
                        Action = "session.stop",
                        DetailsJson = JsonSerializer.Serialize(new { sessionId = session.Id, reason = "auto-close" }),
                    });
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    closedThis = true;
                });
                if (closedThis)
                {
                    closed++;
                }
            }
            catch (ApiException ex) when (ex.Code is "closed")
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Dead-PC auto-close failed for session {SessionId} on station {StationId}", item.Id, item.StationId);
            }
        }

        return closed;
    }
}

public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}

public sealed class DatabaseSeed
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (!await db.StationGroups.AnyAsync(ct))
        {
            db.StationGroups.AddRange(
                new StationGroupEntity { Id = PilotSeed.PcNormalGroupId, Name = "PC Normal", Kind = StationKind.Pc },
                new StationGroupEntity { Id = PilotSeed.PcVipGroupId, Name = "PC VIP", Kind = StationKind.Pc },
                new StationGroupEntity { Id = GameTimerReference.Ps5GroupId, Name = "PS5", Kind = StationKind.Console });
        }

        if (!await db.InvoiceNumbers.AnyAsync(ct))
        {
            db.InvoiceNumbers.Add(new InvoiceNumberSequence { Id = 1, NextNumber = 1 });
        }

        if (!await db.RateLines.AnyAsync(ct))
        {
            db.RateLines.AddRange(RateConfigService.PilotSeedLines());
        }

        if (!await db.BranchRounding.AnyAsync(ct))
        {
            db.BranchRounding.Add(new BranchRoundingEntity
            {
                Id = 1,
                StepToman = 5_000,
                Direction = "Up",
                PcSpecialBandSurchargeToman = RateConfigService.DefaultPcSpecialBandSurchargeToman,
            });
        }

        await db.SaveChangesAsync(ct);

        try
        {
            if (!await db.OperatorUsers.AnyAsync(ct))
            {
                var manager = new OperatorUserEntity { Id = Guid.NewGuid(), Username = "manager", Role = UserRole.Manager };
                manager.PinHash = OperatorAuthService.HashPin(manager, "1234");
                var op = new OperatorUserEntity { Id = Guid.NewGuid(), Username = "operator", Role = UserRole.Operator };
                op.PinHash = OperatorAuthService.HashPin(op, "1111");
                db.OperatorUsers.AddRange(manager, op);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
        }
    }
}
