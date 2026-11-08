using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
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

        var deviceKey = KioskCrypto.NewDeviceKey();
        var device = await db.KioskDevices.FirstOrDefaultAsync(d => d.StationId == row.StationId, ct);
        if (device is null)
        {
            device = new KioskDeviceEntity
            {
                Id = Guid.NewGuid(),
                StationId = row.StationId,
            };
            db.KioskDevices.Add(device);
        }

        device.DeviceKeyHash = KioskCrypto.HashDeviceKey(deviceKey);
        device.LeaseVerifyPublicKeyPem = leaseSigner.PublicKeyPem;
        device.PairedAtUtc = clock.UtcNow.UtcDateTime;
        device.IsRevoked = false;
        device.LastSeenUtc = null;
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
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlRawAsync(
                """SELECT "CustomerId" FROM customer_credentials WHERE "LoginNameNormalized" = {0} FOR UPDATE""",
                normalized);
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
            await db.Database.ExecuteSqlRawAsync(
                """
                UPDATE customer_credentials
                SET "FailedLoginCount" = "FailedLoginCount" + 1,
                    "LockedUntilReset" = CASE WHEN "FailedLoginCount" + 1 >= 5 THEN TRUE ELSE "LockedUntilReset" END
                WHERE "CustomerId" = {0}
                """,
                cred.CustomerId);
            await db.Entry(cred).ReloadAsync(ct);
            if (cred.FailedLoginCount >= 5)
            {
                await audit.AppendAsync("kiosk.login_locked", new { cred.CustomerId }, ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_login", "Invalid login or password.");
        }

            cred.FailedLoginCount = 0;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

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
        });
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
    AppDbContext db,
    RateConfigService rates,
    OperatorMemberContext members,
    SessionBilling billing)
{
    public async Task<SignedSessionLease?> BuildLeaseAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
        {
            return null;
        }

        var lastCheckpoint = session.LastCheckpointUtc ?? session.StartedAtUtc;
        DateTime authorizedUntil;
        long packageMinutes = 0;
        var walletDigest = session.CustomerId?.ToString() ?? "guest";
        if (session.CustomerId is null)
        {
            authorizedUntil = session.PrepaidKind != PrepaidSessionKind.None && session.PrepaidBookedActiveSeconds is long booked
                ? session.StartedAtUtc.AddSeconds(booked)
                : clock.UtcNow.UtcDateTime.Add(DeadPcMonitor.Timeout);
        }
        else
        {
            var settings = await members.LoadSettingsAsync(ct);
            var wallet = MemberSettingsMapper.LoadWalletSettings(settings);
            var (member, packages) = await members.LoadMemberStateAsync(session.CustomerId.Value, ct);
            if (member is null)
            {
                return null;
            }

            var account = KioskPlayEstimate.BuildAccount(member, wallet, packages);
            var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
            var groupId = session.Station?.GroupId ?? PilotSeed.PcNormalGroupId;
            var at = clock.UtcNow;
            var endUtc = session.LastCheckpointUtc ?? clock.UtcNow.UtcDateTime;
            var charge = billing.BuildCharge(session, endUtc, snapshot);
            var (_, estimate) = KioskPlayEstimate.AfterAcceptedPlay(session, member, account, charge, groupId, settings, snapshot.Card, at);
            packageMinutes = estimate.EligiblePackageMinutes;
            var moneyMinutes = estimate.MoneyMinutes ?? 0;
            var debtPlay = wallet.AllowPcWhileInDebt && account.Pc.TotalToman < 0;
            if (debtPlay && packageMinutes <= 0)
            {
                authorizedUntil = lastCheckpoint;
            }
            else if (moneyMinutes <= 0 && estimate.EligiblePackageMinutes <= 0)
            {
                authorizedUntil = lastCheckpoint;
            }
            else
            {
                var playableMinutes = debtPlay && moneyMinutes <= 0
                    ? estimate.EligiblePackageMinutes
                    : Math.Max(0, moneyMinutes) + estimate.EligiblePackageMinutes;
                authorizedUntil = lastCheckpoint.AddMinutes(Math.Max(1, playableMinutes));
            }
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
            MaxBillableCheckpointUtc = lastCheckpoint,
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
    KioskProfileService profiles,
    SessionBilling billing)
{
    public async Task<KioskDisplayDto?> BuildAsync(PlaySessionEntity session, CancellationToken ct)
    {
        var profile = await profiles.EnsureProfileAsync(session.StationId, ct);
        var thresholds = profiles.LoadNearEndMinutes(profile);
        if (session.CustomerId is null)
        {
            var elapsedSeconds = (long)Elapsed(session).TotalSeconds;
            if (session.PrepaidKind != PrepaidSessionKind.None && session.PrepaidBookedActiveSeconds is long booked)
            {
                var remaining = Math.Max(0L, booked - elapsedSeconds);
                return new KioskDisplayDto
                {
                    ElapsedDisplaySeconds = elapsedSeconds,
                    OpenGuestElapsedOnly = false,
                    EstimatedRemainingSeconds = remaining,
                    NearEndMinutesThresholds = thresholds,
                };
            }

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

        var snapshot = await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct);
        var endUtc = session.LastCheckpointUtc ?? clock.UtcNow.UtcDateTime;
        var charge = billing.BuildCharge(session, endUtc, snapshot);
        var account = KioskPlayEstimate.BuildAccount(member, wallet, packages);
        var groupId = session.Station?.GroupId ?? PilotSeed.PcNormalGroupId;
        var at = clock.UtcNow;
        var (settled, estimate) = KioskPlayEstimate.AfterAcceptedPlay(session, member, account, charge, groupId, settings, snapshot.Card, at);
        var packageRemaining = settled.Account.Packages.Select(p => (int)p.RemainingMinutes).ToList();
        var pocket = settled.Account.Pc;
        var elapsed = (long)Elapsed(session).TotalSeconds;
        var moneyMinutes = estimate.MoneyMinutes ?? 0;
        var debtOnly = wallet.AllowPcWhileInDebt && pocket.TotalToman < 0;
        if (debtOnly)
        {
            moneyMinutes = 0;
        }

        int? warning = null;
        var minutesLeft = (int)(estimate.MoneyMinutes ?? 0) + estimate.EligiblePackageMinutes;
        if (minutesLeft > 0)
        {
            warning = thresholds.Where(t => minutesLeft <= t).OrderBy(t => t).FirstOrDefault();
            if (warning == 0)
            {
                warning = null;
            }
        }

        string? freeBand = null;
        if (estimate.RateIsFree)
        {
            freeBand = KioskPlayEstimate.FormatFreeBandText(snapshot.Card, settled.Account, groupId, session, at);
        }

        var moneyOnlySeconds = Math.Max(0, debtOnly ? 0 : moneyMinutes) * 60L;
        var graceActive = settled.Settlement.Grace is { } g
            && clock.UtcNow.UtcDateTime < g.At.UtcDateTime.AddMinutes(1);
        return new KioskDisplayDto
        {
            WalletBalanceToman = settled.Account.Pc.TotalToman,
            GiftBalanceToman = settled.Account.Pc.GiftToman,
            PackageMinutesRemaining = packageRemaining,
            EstimatedRemainingSeconds = moneyOnlySeconds,
            ElapsedDisplaySeconds = elapsed,
            OpenGuestElapsedOnly = false,
            FreeBandText = freeBand,
            GraceActive = graceActive,
            BlockedByDebt = estimate.BlockedByDebt || (debtOnly && estimate.EligiblePackageMinutes <= 0),
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
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (session is not null)
            {
                KioskDeviceAuthorization.EnsureSessionOnDeviceStation(session, device.StationId);
            }
        }
        else
        {
            session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .Where(s => s.StationId == device.StationId && s.Status == SessionStatus.Open)
                .OrderByDescending(s => s.StartedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (session is not null && session.Station?.Group?.Kind == StationKind.Pc
            && session.Status == SessionStatus.Open)
        {
            var lastHeartbeat = await db.KioskHeartbeats
                .Where(h => h.DeviceId == deviceId && h.SessionId == session.Id)
                .OrderByDescending(h => h.ReceivedUtc)
                .Skip(1)
                .FirstOrDefaultAsync(ct);
            var checkpointAt = session.LastCheckpointUtc ?? session.StartedAtUtc;
            var monotonicDropped = clientMonotonicMs == 0
                && checkpointAt.AddMinutes(1) < clock.UtcNow.UtcDateTime
                || lastHeartbeat is not null && clientMonotonicMs + 30_000 < lastHeartbeat.ClientMonotonicMs;
            var monotonicStalled = clientMonotonicMs > 0
                && lastHeartbeat is not null
                && clientMonotonicMs <= lastHeartbeat.ClientMonotonicMs;
            if (!monotonicDropped && !monotonicStalled)
            {
                var last = session.LastCheckpointUtc ?? session.StartedAtUtc;
                foreach (var minute in CheckpointMinutePlanner.MinutesToAccept(last, clock.UtcNow.UtcDateTime))
                {
                    await sessions.RecordCheckpointAsync(session.Id, new DateTimeOffset(minute, TimeSpan.Zero), ct);
                    session = await db.Sessions
                        .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                        .Include(s => s.Segments)
                        .Include(s => s.Pauses)
                        .FirstAsync(s => s.Id == session.Id, ct);
                    last = session.LastCheckpointUtc ?? last;
                }
            }
        }

        if (session is not null)
        {
            session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstAsync(s => s.Id == session.Id, ct);
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
    IClock clock,
    RateConfigService rates,
    SessionService sessions,
    OperatorCustomerService customers,
    KioskPcPrepaidService pcPrepaid,
    KioskProfileService profiles,
    KioskFundingGuard funding,
    KioskCustomerAuthService auth,
    AuditService audit)
{
    public async Task<(Guid CustomerId, PlaySessionEntity Session)> LoginAndAttachAsync(
        string loginName,
        string password,
        string stationId,
        CancellationToken ct)
    {
        var customerId = await auth.LoginAsync(loginName, password, stationId, ct);
        var session = await AttachOrStartMemberAsync(customerId, stationId, ct);
        return (customerId, session);
    }

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

        if (prepaidKind == PrepaidSessionKind.Duration && minutes is int m)
        {
            if (m <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_minutes", "Prepaid minutes must be positive.");
            }
        }
        else if (prepaidKind == PrepaidSessionKind.Amount && amountToman is long a)
        {
            if (a <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Prepaid amount must be positive.");
            }

            var snapshot = await rates.LoadAsync(ct);
            var at = TimeMapping.ToTehran(clock.UtcNow.UtcDateTime);
            var quote = RateResolver.Resolve(snapshot.Card, SpecialDayCalendar.Empty, at, station.GroupId, PlayMode.Offline, 1);
            var oneMinute = MoneyMath.ChargeForMinutes(quote.HourlyRateToman, BillingPolicy.Default.GranularityMinutes, BillingPolicy.Default.Remainder);
            if (a < oneMinute)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "below_minimum", "Amount is below the tariff minimum play time.");
            }
        }

        PlaySessionEntity? session = null;
        try
        {
            session = await sessions.StartAsync(station.Id, 1, PlayMode.Offline, null, ct);
            session = await db.Sessions.Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstAsync(s => s.Id == session.Id, ct);
            if (prepaidKind == PrepaidSessionKind.Duration && minutes is int mins)
            {
                await pcPrepaid.ApplyDurationAsync(session, mins, ct);
            }
            else if (prepaidKind == PrepaidSessionKind.Amount && amountToman is long amt)
            {
                await pcPrepaid.ApplyAmountAsync(session, amt, ct);
            }

            await db.SaveChangesAsync(ct);
        }
        catch
        {
            if (session is not null)
            {
                await RollbackGuestAttemptAsync(session.Id, ct);
            }

            throw;
        }

        await audit.AppendAsync("kiosk.guest_start", new { session.Id, stationId, openPayAtEnd, prepaidKind }, ct);
        return await db.Sessions.Include(s => s.Station).FirstAsync(s => s.Id == session.Id, ct);
    }

    public Task<PlaySessionEntity> AttachOrStartMemberAsync(Guid customerId, string stationId, CancellationToken ct)
        => AttachOrStartMemberCoreAsync(customerId, stationId, ct);

    private async Task<PlaySessionEntity> AttachOrStartMemberCoreAsync(Guid customerId, string stationId, CancellationToken ct)
    {
        var lockKey = BitConverter.ToInt64(customerId.ToByteArray(), 0);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", lockKey);
            try
            {
                var profile = await profiles.EnsureProfileAsync(stationId, ct);
                if (profile.IsTestMode)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "test_mode", "Station is in test mode.");
                }

                await funding.EnsureMemberMayPlayAsync(customerId, stationId, ct);
                var sid = StationIdNormalizer.Normalize(stationId);
                var busyElsewhere = await db.Sessions
                    .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                    .AnyAsync(
                        s => s.CustomerId == customerId
                            && s.StationId != sid
                            && s.Station!.Group!.Kind == StationKind.Pc
                            && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused),
                        ct);
                if (busyElsewhere)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "session_elsewhere", "Customer already has an open PC session.");
                }

                var open = await db.Sessions
                    .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                    .Where(s => s.StationId == sid && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused))
                    .FirstOrDefaultAsync(ct);
                if (open is null)
                {
                    var alreadyOpen = await db.Sessions
                        .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                        .AnyAsync(
                            s => s.CustomerId == customerId
                                && s.Station!.Group!.Kind == StationKind.Pc
                                && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused),
                            ct);
                    if (alreadyOpen)
                    {
                        throw new ApiException(StatusCodes.Status409Conflict, "session_elsewhere", "Customer already has an open PC session.");
                    }

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
            finally
            {
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", lockKey);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private async Task RollbackGuestAttemptAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.Include(s => s.Segments).FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            return;
        }

        if (session.Status == SessionStatus.Open && session.CustomerId is null)
        {
            db.Segments.RemoveRange(session.Segments);
            db.Sessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task LogoutAsync(Guid sessionId, string deviceStationId, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
        KioskDeviceAuthorization.EnsureSessionOnDeviceStation(session, deviceStationId);
        await sessions.StopAsync(sessionId, ct);
    }
}

public sealed class KioskReconcileService(
    AppDbContext db,
    AuditService audit,
    IClock clock,
    SessionService sessions)
{
    public async Task<IReadOnlyList<Guid>> ApplyEventsAsync(Guid deviceId, IReadOnlyList<KioskOutboundEvent> events, CancellationToken ct)
    {
        var device = await db.KioskDevices.FirstAsync(d => d.Id == deviceId, ct);
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
                await HandleOfflineSegmentAsync(device, ev, ct);
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

    private async Task HandleOfflineSegmentAsync(KioskDeviceEntity device, KioskOutboundEvent ev, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(ev.PayloadJson);
        var root = doc.RootElement;
        var sessionId = root.GetProperty("sessionId").GetGuid();
        var powered = root.TryGetProperty("powered", out var p) && p.GetBoolean();
        var segmentEnd = root.TryGetProperty("segmentEndUtc", out var se)
            ? DateTime.Parse(se.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind)
            : clock.UtcNow.UtcDateTime;

        var session = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, reason = "missing_session" }, ct);
            return;
        }

        KioskDeviceAuthorization.EnsureSessionOnDeviceStation(session, device.StationId);

        if (!powered)
        {
            await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, ev.PayloadJson, skipped = "unpowered" }, ct);
            return;
        }

        if (session.Status is SessionStatus.AwaitingPayment or SessionStatus.Closed)
        {
            session.ManualSettleReason = "powered_offline_after_autoclose";
            await audit.AppendAsync("kiosk.offline_after_autoclose", new { sessionId, ev.EventId }, ct);
            await audit.AppendAsync("pc_offline_after_autoclose", new { sessionId, ev.EventId }, ct);
            await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, ev.PayloadJson, manualSettle = true }, ct);
            return;
        }

        if (session.Status != SessionStatus.Open)
        {
            await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, skipped = session.Status.ToString() }, ct);
            return;
        }

        var last = session.LastCheckpointUtc ?? session.StartedAtUtc;
        var cappedEnd = segmentEnd > clock.UtcNow.UtcDateTime ? clock.UtcNow.UtcDateTime : segmentEnd;
        foreach (var minute in CheckpointMinutePlanner.MinutesToAccept(last, cappedEnd))
        {
            await sessions.RecordCheckpointAsync(session.Id, new DateTimeOffset(minute, TimeSpan.Zero), ct);
            await db.Entry(session).ReloadAsync(ct);
            last = session.LastCheckpointUtc ?? last;
        }

        await audit.AppendAsync("kiosk.offline_segment", new { ev.EventId, ev.PayloadJson, reconciled = true }, ct);
    }
}

public sealed class GameCatalogService(AppDbContext db, AuditService audit)
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

    public async Task<GameCatalogEntryEntity> CreateEntryAsync(
        string name,
        int categoryId,
        string executableOrUri,
        string? arguments,
        string? iconUrl,
        int? sortOrder,
        string[]? groupRestrictions,
        string[]? allowedProcessNames,
        CancellationToken ct)
    {
        var version = await db.GameCatalogEntries.MaxAsync(e => (long?)e.CatalogVersion, ct) ?? 0;
        version++;
        var row = new GameCatalogEntryEntity
        {
            Name = name,
            CategoryId = categoryId,
            ExecutableOrUri = executableOrUri,
            Arguments = arguments,
            IconUrl = iconUrl,
            Enabled = true,
            CatalogVersion = version,
            SortOrder = sortOrder ?? (int)version,
            GroupRestrictionsJson = JsonSerializer.Serialize(groupRestrictions ?? []),
            AllowedProcessNamesJson = JsonSerializer.Serialize(allowedProcessNames ?? []),
        };
        db.GameCatalogEntries.Add(row);
        await audit.AppendAsync("game_catalog.create", new { row.Name, row.Id }, ct);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<GameCatalogEntryEntity> UpdateEntryAsync(
        int id,
        string name,
        bool enabled,
        string? executableOrUri,
        string? arguments,
        string? iconUrl,
        int? sortOrder,
        string[]? groupRestrictions,
        string[]? allowedProcessNames,
        CancellationToken ct)
    {
        var row = await db.GameCatalogEntries.FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Game not found.");
        row.Name = name;
        row.Enabled = enabled;
        if (executableOrUri is not null)
        {
            row.ExecutableOrUri = executableOrUri;
        }

        row.Arguments = arguments;
        row.IconUrl = iconUrl;
        if (sortOrder is int order)
        {
            row.SortOrder = order;
        }

        if (groupRestrictions is not null)
        {
            row.GroupRestrictionsJson = JsonSerializer.Serialize(groupRestrictions);
        }

        if (allowedProcessNames is not null)
        {
            row.AllowedProcessNamesJson = JsonSerializer.Serialize(allowedProcessNames);
        }

        row.CatalogVersion = (await db.GameCatalogEntries.MaxAsync(e => (long?)e.CatalogVersion, ct) ?? 0) + 1;
        await audit.AppendAsync("game_catalog.update", new { row.Id, row.Name, row.Enabled }, ct);
        await db.SaveChangesAsync(ct);
        return row;
    }
}
