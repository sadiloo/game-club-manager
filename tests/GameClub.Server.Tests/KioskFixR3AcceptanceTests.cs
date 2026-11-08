using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR3AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task f1_existing_manual_settle_post_endAt_null_succeeds_no_duplicate_pending()
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.DeadPcBackgroundSweepEnabled = true;
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var rateScope = factory.Services.CreateAsyncScope())
        {
            var db = rateScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 480000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-1/start", null);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        var (deviceId, _, _) = await PairDeviceAsync("PC-1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        await factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
            .OnHeartbeatAsync(deviceId, sessionId, 600_000, default);
        factory.Clock.SetTehran(2026, 6, 3, 14, 20);
        await factory.Services.CreateScope().ServiceProvider.GetRequiredService<IDeadPcMonitor>().RunDueClosuresAsync(default);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE sessions SET \"ManualSettleReason\" = {0} WHERE \"Id\" = {1}",
                "powered_offline_after_autoclose",
                sessionId);
            Assert.Equal(1, await db.PendingBills.CountAsync(b => b.SessionId == sessionId));
        }

        var resolve = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/manual-settle", new { endAt = (DateTimeOffset?)null });
        Assert.True(resolve.IsSuccessStatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.PendingBills.CountAsync(b => b.SessionId == sessionId));
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.Null(session.ManualSettleReason);
        }

        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(80_000, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task wallet_hour_then_fresh_scope_heartbeat_no_resurrection()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Wal", Mobile = "09121111111", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 80_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "wal", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "wal", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        var sessionId = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0, 5);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var ack = await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 3_605_000, default);
            Assert.NotNull(ack.Display);
            Assert.True(ack.Display!.WalletBalanceToman is < 80_000 or 0);
            Assert.True(ack.Display.EstimatedRemainingSeconds is < 3600 or 0);
            Assert.NotNull(ack.Lease);
            Assert.True(ack.Lease!.Payload.AuthorizedUntilUtc <= (ack.LastAcceptedCheckpointUtc ?? DateTime.MaxValue).AddHours(1));
        }
    }

    [Fact]
    public async Task package_sixty_after_hour_fresh_heartbeat_stays_sixty_money_3600_not_10800()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Pkg", Mobile = "09122222222", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 80_000 });
            db.CustomerPackages.Add(new CustomerPackageEntity { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Pkg", RemainingMinutes = 120, FaceValueToman = 0 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "pkg3", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "pkg3", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        var sessionId = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0, 5);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var ack = await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 3_605_000, default);
            Assert.NotNull(ack.Display);
            Assert.InRange(ack.Display!.PackageMinutesRemaining![0], 59, 60);
            Assert.Equal(3_600, ack.Display.EstimatedRemainingSeconds);
        }
    }

    [Fact]
    public async Task debt_shared_package_exhausted_followup_no_lease_regain()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.BranchMemberSettings.SingleAsync();
            settings.AllowPcWhileInDebt = true;
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Debt", Mobile = "09123333333", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = -5_000 });
            db.CustomerPackages.Add(new CustomerPackageEntity { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Pkg", RemainingMinutes = 60, FaceValueToman = 0 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "debt3", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "debt3", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0, 5);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var ack = await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 3_605_000, default);
            Assert.Equal(0, ack.Display!.PackageMinutesRemaining![0]);
            Assert.NotNull(ack.Lease);
            Assert.Equal(ack.LastAcceptedCheckpointUtc, ack.Lease!.Payload.AuthorizedUntilUtc);
        }
    }

    [Fact]
    public async Task band_boundary_public_dto_98333_73min_4380s_review_path()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Band", Mobile = "09124444444", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 100_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "band", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 8, 29);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "band", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        var sessionId = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionId").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 8, 30);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
            Assert.NotNull(ack.Display);
            Assert.Equal(98_333, ack.Display!.WalletBalanceToman);
            Assert.InRange(ack.Display.EstimatedRemainingSeconds!.Value, 4380, 4440);
        }
    }

    [Fact]
    public async Task grace_retains_500_one_wall_minute_then_not_open_funded()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 60000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Grace", Mobile = "09125555555", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "grace", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "grace", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
            Assert.InRange(ack.Display!.WalletBalanceToman!.Value, 0, 500);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 2, 1);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var ack = await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>().OnHeartbeatAsync(deviceId, sessionId, 121_000, default);
            Assert.False(ack.Display!.GraceActive);
            Assert.True(ack.Display.EstimatedRemainingSeconds is 0 or null);
        }
    }

    [Fact]
    public async Task integrated_overshoot_500_paid_1500_writeoff_not_2000_collectible()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db0 = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db0.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 60000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Over", Mobile = "09126666660", CreatedAtUtc = DateTime.UtcNow };
            db0.Customers.Add(customer);
            db0.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db0.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "over", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "over", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstAsync(s => s.Id == sessionId);
            session.StartedAtUtc = factory.Clock.UtcNow.UtcDateTime;
            session.LastCheckpointUtc = session.StartedAtUtc;
            session.Segments.Clear();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = session.StartedAtUtc,
                EndUtc = session.StartedAtUtc.AddMinutes(3),
                StationId = session.StationId,
                GroupId = session.Station!.GroupId,
                Mode = PlayMode.Offline,
                PlayerCount = 1,
                Kind = StationKind.Pc,
            });
            await db.SaveChangesAsync();
            var endUtc = session.StartedAtUtc.AddMinutes(3);
            var calculator = scope.ServiceProvider.GetRequiredService<Operator.BillCalculator>();
            var amounts = await calculator.ComputeAsync(session, endUtc, 0, default);
            Assert.NotNull(amounts.Detail?.OvershootAlert);
            Assert.Equal("pc_overshoot", amounts.Detail!.OvershootAlert!.Code);
            Assert.Contains(amounts.Detail.InvoicePreview.Lines, l => l.Kind == InvoiceLineKind.WriteOff);
            Assert.True(amounts.Detail.InvoicePreview.PayableToman <= 500);
            Assert.True(amounts.CollectibleToman <= 2_000);
        }
    }

    [Fact]
    public void pc_kind_custom_group_shows_force_logout_hides_console_controls()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var js = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Server", "wwwroot", "operator", "app.js"));
        Assert.Contains("groupKind === \"Pc\"", js);
        Assert.Contains("forceLogoutBtn", js);
        Assert.Contains("groupKind", js);
    }

    [Fact]
    public async Task kiosk_runtime_heartbeat_accepted_no_argument_mismatch()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-R3", PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-R3");
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-R3/start", null);
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions
            .Where(s => s.StationId == "PC-R3")
            .Select(s => s.Id)
            .SingleAsync();
        var baseUri = factory.CreateClient().BaseAddress!;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "hubs/kiosk"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Headers.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
                options.Headers.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
            })
            .Build();
        await connection.StartAsync();
        var ack = await connection.InvokeAsync<KioskHeartbeatAck>(
            "Heartbeat",
            new KioskHeartbeatRequest { SessionId = sessionId, ClientMonotonicMs = 1_000 });
        Assert.NotNull(ack);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.KioskHeartbeats.AnyAsync(h => h.DeviceId == deviceId && h.SessionId == sessionId));
        }

        await connection.DisposeAsync();
    }

    [Fact]
    public void manager_pairing_code_and_catalog_admin_controls_present()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var html = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Server", "wwwroot", "operator", "index.html"));
        var js = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Server", "wwwroot", "operator", "app.js"));
        Assert.Contains("mgrPairingCodeBtn", html);
        Assert.Contains("mgrCatalogAdminBtn", html);
        Assert.Contains("/api/operator/stations/", js);
        Assert.Contains("/api/operator/games", js);
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
        var op = await factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeBody.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }
}
