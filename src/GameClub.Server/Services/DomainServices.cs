using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public static class TimeMapping
{
    public static DateTimeOffset ToTehran(DateTime utc)
        => TehranTime.FromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public static DateTime ToUtc(DateTimeOffset tehran) => TehranTime.Ensure(tehran).UtcDateTime;
}

public sealed class AuditService(AppDbContext db, IClock clock)
{
    public async Task AppendAsync(string action, object details, CancellationToken ct = default)
    {
        db.AuditLog.Add(new AuditLogEntry
        {
            AtUtc = clock.UtcNow.UtcDateTime,
            Action = action,
            DetailsJson = JsonSerializer.Serialize(details),
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

        if (await db.Stations.AnyAsync(s => EF.Functions.ILike(s.Id, id), ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_station", $"Station '{id}' already exists.");
        }

        var station = new StationEntity { Id = id, Name = name, GroupId = groupId };
        db.Stations.Add(station);
        await audit.AppendAsync("station.register", new { stationId = id, groupId }, ct);
        await db.SaveChangesAsync(ct);
        return station;
    }

    public async Task<StationEntity> UpdateAsync(string stationId, string? name, string? groupId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
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
            var group = await db.StationGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct)
                ?? throw new ApiException(StatusCodes.Status400BadRequest, "unknown_group", $"Unknown group '{groupId}'.");
            station.GroupId = groupId;
        }

        await audit.AppendAsync("station.update", new { stationId = id, name, groupId }, ct);
        await db.SaveChangesAsync(ct);
        return station;
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
        var snapshot = await rates.LoadAsync(ct);
        return BuildCharge(session, endUtc, snapshot);
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

public sealed class SessionCloser(AppDbContext db, RateConfigService rates, SessionBilling billing, AuditService audit)
{
    public async Task<InvoiceEntity> CloseAsync(PlaySessionEntity session, DateTime endUtc, string auditReason, CancellationToken ct)
    {
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

        var snapshot = await rates.LoadAsync(ct);
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

        var number = await NextInvoiceNumberAsync(ct);
        var invoice = new InvoiceEntity
        {
            Id = Guid.NewGuid(),
            Number = number,
            SessionId = session.Id,
            NetBeforeRoundingToman = invoiceCalc.NetBeforeRoundingToman,
            RoundingToman = invoiceCalc.RoundingToman,
            PayableToman = invoiceCalc.PayableToman,
            IssuedAtUtc = endUtc,
        };
        session.Invoice = invoice;
        db.Invoices.Add(invoice);
        await audit.AppendAsync("session.stop", new { sessionId = session.Id, reason = auditReason, invoiceNumber = number }, ct);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    private async Task<long> NextInvoiceNumberAsync(CancellationToken ct)
    {
        var row = await db.InvoiceNumbers.SingleAsync(ct);
        var number = row.NextNumber;
        row.NextNumber = number + 1;
        return number;
    }
}

public sealed class SessionService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    SessionBilling billing,
    SessionCloser closer)
{
    public async Task<PlaySessionEntity> StartAsync(string stationId, int playerCount, PlayMode mode, DateTimeOffset? startedAt, CancellationToken ct)
    {
        var normalizedId = StationIdNormalizer.Normalize(stationId);
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
                    && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused),
                ct))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Station already has an open session.");
        }

        if (playerCount is < 1 or > 4)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_players", "Player count must be 1–4.");
        }

        var startUtc = startedAt.HasValue
            ? TimeMapping.ToUtc(TehranTime.Ensure(startedAt.Value))
            : clock.UtcNow.UtcDateTime;

        var session = new PlaySessionEntity
        {
            Id = Guid.NewGuid(),
            StationId = station.Id,
            Status = SessionStatus.Open,
            StartedAtUtc = startUtc,
            Mode = mode,
            PlayerCount = playerCount,
            LastCheckpointUtc = station.Group.Kind == StationKind.Pc ? startUtc : null,
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
        await audit.AppendAsync("session.start", new { sessionId = session.Id, stationId = station.Id, playerCount, mode = mode.ToString() }, ct);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task PauseAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        if (session.Station!.Group!.Kind == StationKind.Pc)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "pc_pause", "Pause is not allowed on a PC station.");
        }

        if (session.Status == SessionStatus.Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "already_paused", "Session is already paused.");
        }

        var now = clock.UtcNow.UtcDateTime;
        session.Status = SessionStatus.Paused;
        session.Pauses.Add(new PauseIntervalEntity { StartUtc = now });
        await audit.AppendAsync("session.pause", new { sessionId }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ResumeAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        if (session.Status != SessionStatus.Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "Session is not paused.");
        }

        var openPause = session.Pauses.LastOrDefault(p => p.EndUtc is null)
            ?? throw new ApiException(StatusCodes.Status409Conflict, "not_paused", "No open pause.");
        openPause.EndUtc = clock.UtcNow.UtcDateTime;
        session.Status = SessionStatus.Open;
        await audit.AppendAsync("session.resume", new { sessionId }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<InvoiceEntity> StopAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        if (session.Status == SessionStatus.ManualSettle)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
        }

        var endUtc = session.Station!.Group!.Kind == StationKind.Pc
            ? session.LastCheckpointUtc ?? session.StartedAtUtc
            : clock.UtcNow.UtcDateTime;
        return await closer.CloseAsync(session, endUtc, "stop", ct);
    }

    public async Task RecordCheckpointAsync(Guid sessionId, DateTimeOffset? at, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        if (session.Station!.Group!.Kind != StationKind.Pc)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "Checkpoints are for PC sessions only.");
        }

        if (session.Status == SessionStatus.ManualSettle)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "manual_settle", "Session awaits manual settlement.");
        }

        var atUtc = at.HasValue ? TimeMapping.ToUtc(TehranTime.Ensure(at.Value)) : clock.UtcNow.UtcDateTime;
        var previous = session.LastCheckpointUtc ?? session.StartedAtUtc;
        if (atUtc < previous)
        {
            session.Status = SessionStatus.ManualSettle;
            session.ManualSettleReason = "rejected_checkpoint";
            await audit.AppendAsync("session.manual_settle", new { sessionId, reason = "rejected_checkpoint" }, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        session.LastCheckpointUtc = atUtc;
        session.Checkpoints.Add(new CheckpointEntity { AtUtc = atUtc });
        await audit.AppendAsync("session.checkpoint", new { sessionId, atUtc }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ChangePlayerCountAsync(Guid sessionId, int playerCount, CancellationToken ct)
    {
        var session = await LoadOpenSession(sessionId, ct);
        if (session.Station!.Group!.Kind != StationKind.Console)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "Controller count changes apply to consoles only.");
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
        await audit.AppendAsync("session.player_count", new { sessionId, playerCount }, ct);
        await db.SaveChangesAsync(ct);
    }

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

        var endUtc = clock.UtcNow.UtcDateTime;
        var charge = await billing.BuildChargeAsync(session, endUtc, ct);
        return charge.TotalAmountToman;
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
}

public interface IDeadPcMonitor
{
    Task<int> RunDueClosuresAsync(CancellationToken ct);
}

public sealed class DeadPcMonitor(AppDbContext db, IClock clock, SessionCloser closer) : IDeadPcMonitor
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    public async Task<int> RunDueClosuresAsync(CancellationToken ct)
    {
        var now = clock.UtcNow.UtcDateTime;
        var threshold = now - Timeout;
        var candidates = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .Where(s => s.Status == SessionStatus.Open && s.LastCheckpointUtc != null)
            .ToListAsync(ct);

        var due = candidates
            .Where(s => s.Station?.Group?.Kind == StationKind.Pc
                && s.LastCheckpointUtc!.Value <= threshold)
            .ToList();

        foreach (var session in due)
        {
            await closer.CloseAsync(session, session.LastCheckpointUtc!.Value, "auto-close", ct);
        }

        return due.Count;
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
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
