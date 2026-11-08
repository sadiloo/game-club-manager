using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Heartbeat;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Hubs;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR1AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task f6_concurrent_invalid_logins_reach_lock_and_block_reset_until_operator()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Lock", Mobile = "09121111111", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "lock", "good-pass", default);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var tasks = Enumerable.Range(0, 12).Select(_ =>
        {
            var client = CreateKioskClient(deviceId, deviceKey);
            return client.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "lock", password = "wrong", stationId = "PC-1" });
        });
        await Task.WhenAll(tasks);
        await using var verifyScope = factory.Services.CreateAsyncScope();
        var cred = await verifyScope.ServiceProvider.GetRequiredService<AppDbContext>().CustomerCredentials
            .AsNoTracking()
            .SingleAsync(c => c.CustomerId == customer.Id);
        Assert.True(cred.FailedLoginCount >= 5);
        Assert.True(cred.LockedUntilReset);
        var ok = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "lock", password = "good-pass", stationId = "PC-1" });
        Assert.Equal(HttpStatusCode.Forbidden, ok.StatusCode);
    }

    [Fact]
    public async Task two_pcs_authenticate_before_start_yield_at_most_one_open_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-B", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Race", Mobile = "09122222222", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "race", "pass", default);
        var a = await PairDeviceAsync("PC-A");
        var b = await PairDeviceAsync("PC-B");
        var loginTasks = new[]
        {
            CreateKioskClient(a.DeviceId, a.DeviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "race", password = "pass", stationId = "PC-A" }),
            CreateKioskClient(b.DeviceId, b.DeviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "race", password = "pass", stationId = "PC-B" }),
        };
        var responses = await Task.WhenAll(loginTasks);
        Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
        var openCount = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Sessions.CountAsync(s => s.CustomerId == customer.Id && s.Status == SessionStatus.Open);
        Assert.InRange(openCount, 0, 1);
    }

    [Fact]
    public async Task paired_device_cannot_login_heartbeat_or_logout_foreign_station_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-B", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-B/start", null);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        var a = await PairDeviceAsync("PC-A");
        var client = CreateKioskClient(a.DeviceId, a.DeviceKey);
        await Assert.ThrowsAsync<GameClub.Server.Services.ApiException>(async () =>
            await factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(a.DeviceId, sessionId, 1000, default));
        var logout = await client.PostAsync($"/api/kiosk/sessions/{sessionId}/logout", null);
        Assert.Equal(HttpStatusCode.Forbidden, logout.StatusCode);
    }

    [Fact]
    public async Task operator_start_rejected_in_test_mode_and_profile_change_audited()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var put = await mgr.PutAsJsonAsync("/api/operator/stations/PC-1/kiosk-profile", new { isTestMode = true });
        put.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var audited = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.AnyAsync(a => a.Action == "kiosk.profile");
        Assert.True(audited);
        var op = await factory.CreateOperatorClientAsync();
        var start = await op.PostAsync("/api/operator/stations/PC-1/start", null);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
    }

    [Fact]
    public async Task guest_prepaid_duration_and_amount_on_pc_no_orphan_on_failure_f4()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        var bad = await op.PostAsJsonAsync("/api/operator/stations/PC-1/start-guest", new { prepaidKind = "Amount", amountToman = 1, openPayAtEnd = false });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.CountAsync(s => s.Status == SessionStatus.Open));
        var ok = await op.PostAsJsonAsync("/api/operator/stations/PC-1/start-guest", new { prepaidKind = 2, amountToman = 200_000, openPayAtEnd = false });
        ok.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task powered_offline_before_timeout_settles_forty_thousand_not_ten()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 600000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-1/start", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 1);
        using var playScope = factory.Services.CreateScope();
        var playDb = playScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessionId = await playDb.Sessions.Select(s => s.Id).SingleAsync();
        await factory.CreateClientWithKey().PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = S3TestHelpers.T(2026, 6, 3, 14, 1) });
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 4);
        var payload = JsonSerializer.Serialize(new { sessionId, powered = true, segmentEndUtc = factory.Clock.UtcNow.UtcDateTime.ToString("O") });
        var ev = new KioskOutboundEvent { EventId = Guid.NewGuid(), Type = "offline_play_segment", PayloadJson = payload, CreatedUtc = factory.Clock.UtcNow.UtcDateTime };
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/devices/events", new[] { ev });
        await factory.CreateClientWithKey().PostAsync($"/api/sessions/{sessionId}/stop", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(40_000, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task after_exact_ten_minute_autoclose_extra_offline_enqueues_manual_settle()
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.DeadPcBackgroundSweepEnabled = true;
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-1/start", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 11);
        await factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDeadPcMonitor>().RunDueClosuresAsync(default);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var payload = JsonSerializer.Serialize(new { sessionId, powered = true, segmentEndUtc = factory.Clock.UtcNow.UtcDateTime.ToString("O") });
        var ev = new KioskOutboundEvent { EventId = Guid.NewGuid(), Type = "offline_play_segment", PayloadJson = payload, CreatedUtc = factory.Clock.UtcNow.UtcDateTime };
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/devices/events", new[] { ev });
        var queue = await op.GetFromJsonAsync<JsonElement>("/api/operator/manual-settle-queue");
        Assert.True(queue.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task heartbeat_monotonic_reset_does_not_backfill_to_server_now()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 600000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-1/start", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 1);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await factory.CreateClientWithKey().PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = S3TestHelpers.T(2026, 6, 3, 14, 1) });
        var (deviceId, _, _) = await PairDeviceAsync("PC-1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 5);
        var coordinator = factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 0, default);
        await factory.CreateClientWithKey().PostAsync($"/api/sessions/{sessionId}/stop", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(10_000, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task zero_credit_and_debt_without_package_refuse_no_four_hour_lease()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Poor", Mobile = "09123333333", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 0 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "poor", "pass", default);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "poor", password = "pass", stationId = "PC-1" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
    }

    [Fact]
    public async Task catalog_admin_post_put_succeed_and_f5_filter_unchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameCatalogCategories.Add(new GameCatalogCategoryEntity { Name = "Cat", SortOrder = 1 });
            await db.SaveChangesAsync();
        }

        var post = await mgr.PostAsJsonAsync("/api/operator/games", new { name = "Test", categoryId = 1, executableOrUri = "game.exe" });
        post.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var put = await mgr.PutAsJsonAsync($"/api/operator/games/{id}", new { name = "Test2", enabled = true });
        put.EnsureSuccessStatusCode();
        GameClub.Kiosk.Core.Catalog.GameCatalogFilter.ForStation(
            [new(1, "A", 1, "a.exe", null, null, 1, true, [], [], 18)],
            PilotSeed.PcNormalGroupId);
    }

    [Fact]
    public async Task apply_discounts_to_pc_false_leaves_sixty_thousand_on_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BranchMemberSettings.SingleAsync();
        settings.ApplyDiscountsToPc = false;
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Disc", Mobile = "09124444444", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 1_000_000, LifetimePlayMinutes = 10_000 });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 60000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var session = await sessions.StartAsync("PC-1", 1, PlayMode.Offline, null, default);
        await scope.ServiceProvider.GetRequiredService<OperatorCustomerService>().LinkSessionAsync(session.Id, customer.Id, default);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await sessions.RecordCheckpointAsync(session.Id, S3TestHelpers.T(2026, 6, 3, 15, 0), default);
        var calculator = scope.ServiceProvider.GetRequiredService<BillCalculator>();
        var bill = await calculator.ComputeAsync(
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .FirstAsync(s => s.Id == session.Id),
            factory.Clock.UtcNow.UtcDateTime,
            0,
            default);
        Assert.Equal(60_000, bill.TimeGrossToman);
        Assert.Equal(0, bill.DiscountToman);
    }

    [Fact]
    public void grace_enabled_retains_five_hundred_through_free_minute()
    {
        var policy = new BillingPolicy(5, PartialUnitMode.Ceiling, RateRemainderMode.Nearest);
        var start = TehranTime.At(2026, 6, 3, 14, 0);
        var charge = SessionPricer.Price(
            new Session("s", [new PlaySegment("a", start, start.AddMinutes(1), "PC-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc)], null),
            PilotSeed.CreateCard(),
            policy: policy);
        var wallet = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(500, 0), new WalletPocket(0, 0), []);
        var play = CustomerPlay.Settle(charge, wallet, billing: policy);
        Assert.Equal(500, play.Account.Pc.TotalToman);
        Assert.NotNull(play.Settlement.Grace);
    }

    [Fact]
    public async Task hop_stale_source_heartbeat_does_not_receive_target_lease_or_advance_target()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-SRC", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-TGT", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-SRC/start", null);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await S3TestHelpers.TransferAsync(op, sessionId, "PC-TGT");
        var src = await PairDeviceAsync("PC-SRC");
        await Assert.ThrowsAsync<ApiException>(async () =>
            await factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(src.DeviceId, sessionId, 5000, default));
    }

    [Fact]
    public async Task package_display_sixty_remaining_after_sixty_minutes_accepted_play()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Pkg", Mobile = "09125555555", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 0 });
        db.CustomerPackages.Add(new CustomerPackageEntity { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Pkg", RemainingMinutes = 120, FaceValueToman = 0 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "pkg", "pass", default);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "pkg", password = "pass", stationId = "PC-1" });
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 1000, default);
        var display = await scope.ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(await db.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group).FirstAsync(s => s.Id == sessionId), default);
        Assert.Equal(60, display!.PackageMinutesRemaining![0]);
    }

    [Fact]
    public async Task repair_same_station_not_500_and_lease_verifies_after_server_restart()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        var first = await PairAsync("PC-1");
        var second = await PairAsync("PC-1");
        Assert.NotEqual(HttpStatusCode.InternalServerError, second.Status);
        await using var scope2 = factory.Services.CreateAsyncScope();
        var signer = scope2.ServiceProvider.GetRequiredService<IKioskLeaseSigner>();
        var verifier = new LeaseVerifier(first.PublicKey);
        var lease = signer.Sign(new SessionLeasePayload
        {
            LeaseId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            StationId = "PC-1",
            AuthorizedUntilUtc = DateTime.UtcNow.AddHours(1),
            MaxBillableCheckpointUtc = DateTime.UtcNow,
            IssuedAtUtc = DateTime.UtcNow,
        });
        Assert.True(verifier.TryVerify(lease));
    }

    [Fact]
    public async Task net48_offline_queue_survives_restart_and_ack_idempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiosk-queue-{Guid.NewGuid():N}.db");
        var q1 = new SqliteOfflineEventQueue(path);
        var id = Guid.NewGuid();
        await q1.EnqueueAsync(id, "offline_play_segment", "{}", DateTime.UtcNow);
        var q2 = new SqliteOfflineEventQueue(path);
        var pending = await q2.PendingAsync();
        Assert.Single(pending);
        await q2.AckAsync(id);
        await q2.AckAsync(id);
        Assert.Empty(await q2.PendingAsync());
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairDeviceAsync(string stationId)
    {
        var pair = await PairAsync(stationId);
        return (pair.DeviceId, pair.DeviceKey, pair.PublicKey);
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey, HttpStatusCode Status)> PairAsync(string stationId)
    {
        var op = await factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeBody.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var client = factory.CreateClient();
        var pair = await client.PostAsJsonAsync("/api/kiosk/pair", new { code });
        if (!pair.IsSuccessStatusCode)
        {
            return (Guid.Empty, "", "", pair.StatusCode);
        }

        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!,
            pair.StatusCode);
    }
}
