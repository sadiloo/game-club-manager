using GameClub.Server.Data;
using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;
using GameClub.Kiosk.Core.Admin;

namespace GameClub.Server.Kiosk;

public sealed class KioskOperatorLockService(
    AppDbContext db,
    SessionService sessions,
    AuditService audit,
    IClock clock)
{
    public const int MaxIdleMinutes = KioskOpStrings.MaxFreeUseIdleMinutes;

    public async Task LockOpenSessionAsync(string stationId, Guid sessionId, Guid kioskDeviceId, CancellationToken ct)
    {
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stationId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
        if (station.Group?.Kind != StationKind.Pc)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "Only PC kiosks support operator lock.");
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.StationId == stationId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        if (session.Status != SessionStatus.Open)
        {
            return;
        }

        await sessions.StopAsync(sessionId, ct);
        await audit.AppendAsync(
            "kiosk.operator_lock",
            new { stationId, sessionId, kioskDeviceId, reason = "kiosk_operator_lock" },
            ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> StartFreeUseAsync(string stationId, Guid kioskDeviceId, string actorRole, CancellationToken ct)
    {
        await EnsurePcStationAsync(stationId, ct);
        await EnsureUnlockAllowedAsync(stationId, ct);

        var active = await GetOpenFreeUseAsync(stationId, ct);
        if (active is not null)
        {
            return active.Id;
        }

        var row = new KioskFreeUseEntity
        {
            Id = Guid.NewGuid(),
            StationId = stationId,
            StartedAtUtc = clock.UtcNow.UtcDateTime,
            KioskDeviceId = kioskDeviceId,
            ActorRole = NormalizeActorRole(actorRole),
        };
        db.KioskFreeUses.Add(row);
        await audit.AppendAsync("kiosk.free_use.start", new { stationId, freeUseId = row.Id, kioskDeviceId, actorRole = row.ActorRole }, ct);
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    public async Task EndFreeUseAsync(string stationId, Guid freeUseId, string reason, CancellationToken ct)
        => await EndFreeUseAsync(stationId, freeUseId, reason, endedAtUtc: null, ct);

    public async Task EndFreeUseAsync(string stationId, Guid freeUseId, string reason, DateTime? endedAtUtc, CancellationToken ct)
    {
        var row = await db.KioskFreeUses.FirstOrDefaultAsync(
            r => r.Id == freeUseId && r.StationId == stationId,
            ct);
        if (row is null || row.EndedAtUtc is not null)
        {
            return;
        }

        row.EndedAtUtc = endedAtUtc ?? clock.UtcNow.UtcDateTime;
        row.EndReason = reason;
        await audit.AppendAsync("kiosk.free_use.end", new { stationId, freeUseId, reason }, ct);
        await db.SaveChangesAsync(ct);
    }

    public Task<KioskFreeUseEntity?> GetOpenFreeUseAsync(string stationId, CancellationToken ct)
        => db.KioskFreeUses.AsNoTracking()
            .FirstOrDefaultAsync(r => r.StationId == stationId && r.EndedAtUtc == null, ct);

    /// <summary>Read path only: normalize corrupt stored N (spec v2.9 §13.9 و-۴).</summary>
    public async Task<int> GetIdleMinutesAsync(CancellationToken ct)
    {
        var row = await db.BranchCafeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var minutes = row?.KioskFreeUseIdleMinutes ?? 5;
        if (minutes < 1)
        {
            minutes = 5;
        }

        return Math.Min(minutes, MaxIdleMinutes);
    }

  /// <summary>Write path: reject out-of-range N (spec v2.9 §13.5); no clamping on save.</summary>
    public static int ValidateIdleMinutes(int minutes)
    {
        if (minutes < 1 || minutes > MaxIdleMinutes)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                KioskFreeIdleSettingCodes.Invalid,
                KioskOpStrings.MgrFreeIdleErrInvalid);
        }

        return minutes;
    }

    private async Task EnsureUnlockAllowedAsync(string stationId, CancellationToken ct)
    {
        var station = await db.Stations.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stationId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");

        if (station.OperationalState == StationOperationalState.Broken)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "station_broken", "admin.pcUnlock.reason.broken");
        }

        var paired = await db.KioskDevices.AsNoTracking()
            .AnyAsync(d => d.StationId == stationId && !d.IsRevoked, ct);
        if (!paired)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "unpaired", "admin.pcUnlock.reason.unpaired");
        }

        var openSession = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.StationId == stationId && s.Status == SessionStatus.Open, ct);
        if (openSession)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "session_open", "kiosk.oplock.err.session_open");
        }

        var awaiting = await db.Sessions.AsNoTracking()
            .AnyAsync(
                s => s.StationId == stationId
                    && (s.Status == SessionStatus.AwaitingPayment
                        || s.Status == SessionStatus.BillOpen
                        || s.Status == SessionStatus.ManualSettle),
                ct);
        if (awaiting)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "awaiting_settlement", "admin.pcUnlock.reason.awaiting");
        }
    }

    private static string NormalizeActorRole(string role)
        => role?.ToLowerInvariant() switch
        {
            "manager" => "manager",
            _ => "operator",
        };

    private async Task EnsurePcStationAsync(string stationId, CancellationToken ct)
    {
        var station = await db.Stations.Include(s => s.Group).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == stationId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
        if (station.Group?.Kind != StationKind.Pc)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "Only PC kiosks support operator lock.");
        }
    }
}