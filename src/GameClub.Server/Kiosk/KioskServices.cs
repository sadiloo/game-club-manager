using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Heartbeat;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskPairingService(AppDbContext db, IClock clock, IKioskLeaseSigner leaseSigner, AuditService audit)
{
    public async Task<object> CreatePairingCodeAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        _ = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Station not found.");
        var code = Random.Shared.Next(100_000, 999_999).ToString();
        var row = new KioskPairingCodeEntity
        {
            StationId = id,
            Code = code,
            ExpiresAtUtc = clock.UtcNow.UtcDateTime.AddMinutes(10),
        };
        db.KioskPairingCodes.Add(row);
        await db.SaveChangesAsync(ct);
        return new { code, expiresAtUtc = row.ExpiresAtUtc };
    }

    public async Task<object> PairAsync(string code, CancellationToken ct)
    {
        var row = await db.KioskPairingCodes
            .Include(p => p.Station)
            .Where(p => p.Code == code && p.ConsumedAtUtc == null)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiException(StatusCodes.Status400BadRequest, "invalid_code", "Pairing code is invalid.");
        if (row.ExpiresAtUtc < clock.UtcNow.UtcDateTime)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "expired_code", "Pairing code expired.");
        }

        var existing = await db.KioskDevices.FirstOrDefaultAsync(d => d.StationId == row.StationId, ct);
        if (existing is not null)
        {
            existing.IsRevoked = true;
        }

        var deviceKey = KioskCrypto.NewDeviceKey();
        var device = new KioskDeviceEntity
        {
            Id = Guid.NewGuid(),
            StationId = row.StationId,
            DeviceKeyHash = KioskCrypto.HashDeviceKey(deviceKey),
            LeaseVerifyPublicKeyPem = leaseSigner.PublicKeyPem,
            PairedAtUtc = clock.UtcNow.UtcDateTime,
        };
        db.KioskDevices.Add(device);
        row.ConsumedAtUtc = clock.UtcNow.UtcDateTime;
        row.ConsumedDeviceId = device.Id;
        await audit.AppendAsync("kiosk.pair", new { device.Id, row.StationId }, ct);
        await db.SaveChangesAsync(ct);
        return new
        {
            deviceId = device.Id,
            deviceKey,
            stationId = device.StationId,
            leaseVerifyPublicKeyPem = device.LeaseVerifyPublicKeyPem,
        };
    }
}

public sealed class KioskCustomerAuthService(AppDbContext db, AuditService audit)
{
    public async Task<Guid> LoginAsync(string loginName, string password, string stationId, CancellationToken ct)
    {
        var normalized = KioskCrypto.NormalizeLoginName(loginName);
        var cred = await db.CustomerCredentials
            .Include(c => c.Customer)
            .FirstOrDefaultAsync(c => c.LoginNameNormalized == normalized, ct);
        if (cred is null)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_login", "Invalid login or password.");
        }

        if (cred.LockedUntilReset)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "account_locked", "Account locked; operator reset required."); // F6
        }

        if (!KioskCrypto.VerifyPassword(password, cred.PasswordHash))
        {
            cred.FailedLoginCount++;
            if (cred.FailedLoginCount >= 5)
            {
                cred.LockedUntilReset = true;
                await audit.AppendAsync("kiosk.login_locked", new { cred.CustomerId }, ct);
            }

            await db.SaveChangesAsync(ct);
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_login", "Invalid login or password.");
        }

        cred.FailedLoginCount = 0;
        await db.SaveChangesAsync(ct);

        var sid = StationIdNormalizer.Normalize(stationId);
        var otherOpen = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .AnyAsync(
                s => s.CustomerId == cred.CustomerId
                    && s.StationId != sid
                    && s.Station!.Group!.Kind == StationKind.Pc
                    && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused),
                ct);
        if (otherOpen)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "session_elsewhere", "Customer already has an open PC session.");
        }

        return cred.CustomerId;
    }

    public async Task SetCredentialAsync(Guid customerId, string loginName, string newPassword, CancellationToken ct)
    {
        _ = await db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Customer not found.");
        var normalized = KioskCrypto.NormalizeLoginName(loginName);
        var hash = KioskCrypto.HashPassword(newPassword);
        var existing = await db.CustomerCredentials.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);
        if (existing is null)
        {
            db.CustomerCredentials.Add(new CustomerCredentialEntity
            {
                CustomerId = customerId,
                LoginNameNormalized = normalized,
                LoginNameDisplay = loginName.Trim(),
                PasswordHash = hash,
            });
        }
        else
        {
            existing.LoginNameNormalized = normalized;
            existing.LoginNameDisplay = loginName.Trim();
            existing.PasswordHash = hash;
            existing.FailedLoginCount = 0;
            existing.LockedUntilReset = false;
        }

        await db.SaveChangesAsync(ct);
    }
}

public sealed class KioskProfileService(AppDbContext db)
{
    public async Task<StationKioskProfileEntity> EnsureProfileAsync(string stationId, CancellationToken ct)
    {
        var id = StationIdNormalizer.Normalize(stationId);
        var row = await db.StationKioskProfiles.FirstOrDefaultAsync(p => p.StationId == id, ct);
        if (row is not null)
        {
            return row;
        }

        row = new StationKioskProfileEntity { StationId = id, NearEndMinutesJson = "[10,5,1]" };
        db.StationKioskProfiles.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public IReadOnlyList<int> LoadNearEndMinutes(StationKioskProfileEntity profile)
    {
        if (string.IsNullOrWhiteSpace(profile.NearEndMinutesJson))
        {
            return [10, 5, 1];
        }

        return JsonSerializer.Deserialize<int[]>(profile.NearEndMinutesJson) ?? [10, 5, 1];
    }
}

public sealed class KioskLeaseService(
    IClock clock,
    IKioskLeaseSigner signer,
    AppDbContext db)
{
    public async Task<SignedSessionLease?> BuildLeaseAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
        {
            return null;
        }

        var authorizedUntil = (session.LastCheckpointUtc ?? session.StartedAtUtc).AddHours(4);
        var walletDigest = session.CustomerId?.ToString() ?? "guest";
        long packageMinutes = 0;
        if (session.CustomerId is Guid cid)
        {
            var pkgs = await db.CustomerPackages.Where(p => p.CustomerId == cid).ToListAsync(ct);
            packageMinutes = pkgs.Sum(p => p.RemainingMinutes);
        }

        var catalogVersion = await db.GameCatalogEntries.Select(e => (long?)e.CatalogVersion).MaxAsync(ct) ?? 0;
        var payload = new SessionLeasePayload
        {
            LeaseId = Guid.NewGuid(),
            SessionId = session.Id,
            StationId = session.StationId,
            CustomerId = session.CustomerId,
            PrepaidKind = session.PrepaidKind.ToString(),
            AuthorizedUntilUtc = authorizedUntil,
            MaxBillableCheckpointUtc = session.LastCheckpointUtc ?? session.StartedAtUtc,
            WalletDigest = walletDigest,
            PackageMinutesTotal = packageMinutes,
            CatalogVersion = catalogVersion,
            IssuedAtUtc = clock.UtcNow.UtcDateTime,
            LeaseVersion = 1,
        };
        return signer.Sign(payload);
    }
}

public sealed class KioskDisplayService(
    IClock clock,
    RateConfigService rates,
    OperatorMemberContext members,
    KioskProfileService profiles)
{
    public async Task<KioskDisplayDto?> BuildAsync(PlaySessionEntity session, CancellationToken ct)
    {
        var profile = await profiles.EnsureProfileAsync(session.StationId, ct);
        var thresholds = profiles.LoadNearEndMinutes(profile);
        if (session.CustomerId is null)
        {
            var elapsedSeconds = (long)Elapsed(session).TotalSeconds;
            return new KioskDisplayDto
            {
                ElapsedDisplaySeconds = elapsedSeconds,
                OpenGuestElapsedOnly = true,
                NearEndMinutesThresholds = thresholds,
            };
        }

        var settings = await members.LoadSettingsAsync(ct);
        var wallet = MemberSettingsMapper.LoadWalletSettings(settings);
        var (member, packages) = await members.LoadMemberStateAsync(session.CustomerId, ct);
        if (member is null)
        {
            return null;
        }

        var pocket = MemberWalletMapper.ToPocket(member);
        var account = new CustomerAccount(member.CustomerId.ToString(), wallet, pocket, new WalletPocket(0, 0), packages);
        var snapshot = await rates.LoadAsync(ct);
        var groupId = session.Station?.GroupId ?? PilotSeed.PcNormalGroupId;
        var at = clock.UtcNow;
        var estimate = CustomerPlay.Estimate(account, groupId, session.Mode, session.PlayerCount, at, snapshot.Card);
        var elapsed = (long)Elapsed(session).TotalSeconds;
        int? warning = null;
        if (estimate.TotalMinutes > 0)
        {
            var minutesLeft = (int)estimate.TotalMinutes;
            warning = thresholds.Where(t => minutesLeft <= t).OrderBy(t => t).FirstOrDefault();
            if (warning == 0)
            {
                warning = null;
            }
        }

        return new KioskDisplayDto
        {
            WalletBalanceToman = pocket.RealToman,
            GiftBalanceToman = pocket.GiftToman,
            PackageMinutesRemaining = packages.Select(p => (int)p.RemainingMinutes).ToList(),
            EstimatedRemainingSeconds = estimate.TotalMinutes * 60,
            ElapsedDisplaySeconds = elapsed,
            BlockedByDebt = estimate.BlockedByDebt,
            NearEndMinutesThresholds = thresholds,
            WarningRemainingMinutes = warning,
        };
    }

    private TimeSpan Elapsed(PlaySessionEntity session)
    {
        var end = session.EndedAtUtc ?? clock.UtcNow.UtcDateTime;
        return end - session.StartedAtUtc;
    }
}

public sealed class KioskCheckpointCoordinator(
    AppDbContext db,
    IClock clock,
    SessionService sessions,
    KioskLeaseService leases,
    KioskDisplayService display)
{
    public async Task<KioskHeartbeatAck> OnHeartbeatAsync(Guid deviceId, Guid? sessionId, long clientMonotonicMs, CancellationToken ct)
    {
        var device = await db.KioskDevices.FirstAsync(d => d.Id == deviceId, ct);
        device.LastSeenUtc = clock.UtcNow.UtcDateTime;
        db.KioskHeartbeats.Add(new KioskHeartbeatEntity
        {
            DeviceId = deviceId,
            SessionId = sessionId,
            ReceivedUtc = clock.UtcNow.UtcDateTime,
            ClientMonotonicMs = clientMonotonicMs,
        });
        await db.SaveChangesAsync(ct);

        PlaySessionEntity? session = null;
        if (sessionId is Guid sid)
        {
            session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstOrDefaultAsync(s => s.Id == sid, ct);
        }
        else
        {
            session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Where(s => s.StationId == device.StationId && s.Status == SessionStatus.Open)
                .OrderByDescending(s => s.StartedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (session is not null && session.Station?.Group?.Kind == StationKind.Pc)
        {
            var last = session.LastCheckpointUtc ?? session.StartedAtUtc;
            foreach (var minute in CheckpointMinutePlanner.MinutesToAccept(last, clock.UtcNow.UtcDateTime))
            {
                await sessions.RecordCheckpointAsync(session.Id, new DateTimeOffset(minute, TimeSpan.Zero), ct);
                await db.Entry(session).ReloadAsync(ct);
                last = session.LastCheckpointUtc ?? last;
            }
        }

        SignedSessionLease? lease = session is not null ? await leases.BuildLeaseAsync(session, ct) : null;
        var displayDto = session is not null ? await display.BuildAsync(session, ct) : null;
        var manualSettle = session?.Status == SessionStatus.ManualSettle;
        DateTime? deadLine = session?.LastCheckpointUtc?.Add(DeadPcMonitor.Timeout);
        return new KioskHeartbeatAck
        {
            ServerUtc = clock.UtcNow.UtcDateTime,
            LastAcceptedCheckpointUtc = session?.LastCheckpointUtc,
            SessionStatus = session?.Status.ToString() ?? "None",
            Lease = lease,
            Display = displayDto,
            ManualSettle = manualSettle,
            DeadPcDeadlineUtc = deadLine,
        };
    }
}

public sealed class KioskSessionService(
    AppDbContext db,
    SessionService sessions,
    OperatorCustomerService customers,
    OperatorPrepaidService prepaid,
    KioskProfileService profiles,
    AuditService audit)
{
    public async Task<PlaySessionEntity> StartGuestAsync(
        string stationId,
        PrepaidSessionKind? prepaidKind,
        int? minutes,
        long? amountToman,
        bool openPayAtEnd,
        CancellationToken ct)
    {
        var profile = await profiles.EnsureProfileAsync(stationId, ct);
        if (profile.IsTestMode)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "test_mode", "Station is in test mode.");
        }

        var station = await db.Stations.Include(s => s.Group).FirstAsync(s => s.Id == StationIdNormalizer.Normalize(stationId), ct);
        if (station.Group?.Kind != StationKind.Pc)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc", "Guest start is for PC stations only.");
        }

        var session = await sessions.StartAsync(station.Id, 1, PlayMode.Offline, null, ct);
        if (prepaidKind == PrepaidSessionKind.Duration && minutes is int m)
        {
            await prepaid.StartDurationAsync(session.Id, m, ct);
        }
        else if (prepaidKind == PrepaidSessionKind.Amount && amountToman is long a)
        {
            await prepaid.StartAmountAsync(session.Id, a, ct);
        }

        await audit.AppendAsync("kiosk.guest_start", new { session.Id, stationId, openPayAtEnd, prepaidKind }, ct);
        return await db.Sessions.Include(s => s.Station).FirstAsync(s => s.Id == session.Id, ct);
    }

    public async Task<PlaySessionEntity> AttachOrStartMemberAsync(Guid customerId, string stationId, CancellationToken ct)
    {
        var profile = await profiles.EnsureProfileAsync(stationId, ct);
        if (profile.IsTestMode)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "test_mode", "Station is in test mode.");
        }

        var sid = StationIdNormalizer.Normalize(stationId);
        var open = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Where(s => s.StationId == sid && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused))
            .FirstOrDefaultAsync(ct);
        if (open is null)
        {
            var session = await sessions.StartAsync(sid, 1, PlayMode.Offline, null, ct);
            await customers.LinkSessionAsync(session.Id, customerId, ct);
            return await db.Sessions.FirstAsync(s => s.Id == session.Id, ct);
        }

        if (open.CustomerId is Guid existing && existing != customerId)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "station_busy", "Station has another customer's session.");
        }

        if (open.CustomerId is null)
        {
            await customers.LinkSessionAsync(open.Id, customerId, ct);
        }

        return open;
    }

    public Task LogoutAsync(Guid sessionId, CancellationToken ct)
        => sessions.StopAsync(sessionId, ct);
}

public sealed class KioskReconcileService(AppDbContext db, AuditService audit)
{
    public async Task<IReadOnlyList<Guid>> ApplyEventsAsync(Guid deviceId, IReadOnlyList<KioskOutboundEvent> events, CancellationToken ct)
    {
        var acked = new List<Guid>();
        foreach (var ev in events)
        {
            if (await db.AuditLog.AnyAsync(a => a.DetailsJson.Contains(ev.EventId.ToString()), ct))
            {
                acked.Add(ev.EventId);
                continue;
            }

            if (ev.Type == "offline_play_segment")
            {
                await HandleOfflineSegmentAsync(ev, ct);
            }
            else if (ev.Type == "process_violation" || ev.Type == "client_lock_grace_exhausted")
            {
                await audit.AppendAsync(ev.Type, new { ev.EventId, ev.PayloadJson }, ct);
            }

            acked.Add(ev.EventId);
        }

        await db.SaveChangesAsync(ct);
        return acked;
    }

    private async Task HandleOfflineSegmentAsync(KioskOutboundEvent ev, CancellationToken ct)
    {
        // F1: after auto-close, route extra powered-offline time to manual settle instead of billing.
        await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, ev.PayloadJson }, ct);
    }
}

public sealed class GameCatalogService(AppDbContext db)
{
    public async Task<object> GetCatalogAsync(long sinceVersion, CancellationToken ct)
    {
        var entries = await db.GameCatalogEntries.AsNoTracking()
            .Where(e => e.CatalogVersion > sinceVersion)
            .OrderBy(e => e.SortOrder)
            .ToListAsync(ct);
        var version = entries.Count == 0
            ? await db.GameCatalogEntries.MaxAsync(e => (long?)e.CatalogVersion, ct) ?? 0
            : entries.Max(e => e.CatalogVersion);
        return new
        {
            catalogVersion = version,
            entries = entries.Select(e => new
            {
                e.Id,
                e.Name,
                e.CategoryId,
                e.ExecutableOrUri,
                e.Arguments,
                e.IconUrl,
                e.SortOrder,
                e.Enabled,
                allowedProcessNames = JsonSerializer.Deserialize<string[]>(e.AllowedProcessNamesJson) ?? [],
                groupRestrictions = JsonSerializer.Deserialize<string[]>(e.GroupRestrictionsJson) ?? [],
                e.MinAge,
            }),
        };
    }
}
