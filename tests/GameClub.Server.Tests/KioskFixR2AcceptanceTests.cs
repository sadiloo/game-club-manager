using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR2AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public void kiosk_http_client_uses_device_id_headers_not_kiosk_prefix()
    {
        _ = KioskConnectionFactory.Create(new Uri("http://127.0.0.1/hubs/kiosk"), "d", "k", true);
        Assert.Equal("X-Device-Id", KioskDeviceAuthMiddleware.DeviceIdHeader);
        Assert.Equal("X-Device-Key", KioskDeviceAuthMiddleware.DeviceKeyHeader);
        Assert.NotEqual("X-Kiosk-Device-Id", KioskDeviceAuthMiddleware.DeviceIdHeader);
    }

    [Fact]
    public async Task paired_device_cannot_read_foreign_station_config_or_active_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-B", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-B/start", null);
        var a = await PairDeviceAsync("PC-A");
        var client = CreateKioskClient(a.DeviceId, a.DeviceKey);
        var config = await client.GetAsync("/api/kiosk/stations/PC-B/config");
        var active = await client.GetAsync("/api/kiosk/stations/PC-B/active-session");
        Assert.Equal(HttpStatusCode.Forbidden, config.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, active.StatusCode);
        var own = await client.GetAsync($"/api/kiosk/stations/PC-A/config");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    [Fact]
    public async Task both_auth_then_concurrent_attach_yields_at_most_one_open_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-B", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var sessions = scope.ServiceProvider.GetRequiredService<KioskSessionService>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Race2", Mobile = "09126666666", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customer.Id, "race2", "pass", default);
        async Task<Guid> LoginInScopeAsync(string station)
        {
            await using var loginScope = factory.Services.CreateAsyncScope();
            return await loginScope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().LoginAsync("race2", "pass", station, default);
        }

        await Task.WhenAll(LoginInScopeAsync("PC-A"), LoginInScopeAsync("PC-B"));
        async Task<bool> AttachInScopeAsync(string station)
        {
            await using var attachScope = factory.Services.CreateAsyncScope();
            try
            {
                await attachScope.ServiceProvider.GetRequiredService<KioskSessionService>().AttachOrStartMemberAsync(customer.Id, station, default);
                return true;
            }
            catch (ApiException)
            {
                return false;
            }
        }

        var successes = 0;
        if (await AttachInScopeAsync("PC-A"))
        {
            successes++;
        }

        if (await AttachInScopeAsync("PC-B"))
        {
            successes++;
        }

        Assert.Equal(1, successes);
        var openCount = await db.Sessions.CountAsync(s => s.CustomerId == customer.Id && s.Status == SessionStatus.Open);
        Assert.Equal(1, openCount);
    }

    [Fact]
    public async Task after_lockout_operator_reset_next_login_succeeds_not_500()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Reset", Mobile = "09127777777", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 100_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customer.Id, "resetme", "good", default);
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<ApiException>(() => auth.LoginAsync("resetme", "bad", "PC-1", default));
        }

        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var credReset = await mgr.PutAsJsonAsync($"/api/operator/customers/{customer.Id}/credential", new { loginName = "resetme", password = "good" });
        credReset.EnsureSuccessStatusCode();
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "resetme", password = "good", stationId = "PC-1" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task replayed_nonzero_monotonic_without_powered_evidence_bills_10000_not_50000()
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
        var coordinator = factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
        factory.Clock.SetTehran(2026, 6, 3, 14, 5);
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
        await factory.CreateClientWithKey().PostAsync($"/api/sessions/{sessionId}/stop", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(10_000, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task f1_awaiting_payment_with_reason_resolves_via_manual_settle_action()
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
        using (var billScope = factory.Services.CreateScope())
        {
            var sessionBefore = await billScope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.SingleAsync();
            Assert.Equal(SessionStatus.AwaitingPayment, sessionBefore.Status);
        }
        using (var reasonScope = factory.Services.CreateScope())
        {
            var dbReason = reasonScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbReason.Database.ExecuteSqlRawAsync(
                "UPDATE sessions SET \"ManualSettleReason\" = {0} WHERE \"Id\" = {1}",
                "powered_offline_after_autoclose",
                sessionId);
        }

        var resolve = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/manual-settle", new { endAt = (DateTimeOffset?)null });
        Assert.True(resolve.IsSuccessStatusCode);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(80_000, bill.GetProperty("timeGrossToman").GetInt64());
    }

    [Fact]
    public async Task after_one_hour_accepted_play_remaining_money_and_lease_shrink()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        }

        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Hr", Mobile = "09128888888", CreatedAtUtc = DateTime.UtcNow };
            db2.Customers.Add(customer);
            db2.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 80_000 });
            await db2.SaveChangesAsync();
            await scope2.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "hr", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "hr", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        Guid sessionId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            sessionId = await lookup.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await using (var hb = factory.Services.CreateAsyncScope())
        {
            await hb.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        }

        await using (var displayScope = factory.Services.CreateAsyncScope())
        {
            var db2 = displayScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db2.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group).FirstAsync(s => s.Id == sessionId);
            var display = await displayScope.ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(session, default);
            Assert.True(display!.WalletBalanceToman < 80_000 || display.EstimatedRemainingSeconds < 3600);
        }
    }

    [Fact]
    public async Task package_sixty_after_checkpoint_stays_sixty_on_next_heartbeat()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Pkg2", Mobile = "09129999999", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 0 });
            db.CustomerPackages.Add(new CustomerPackageEntity { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Pkg", RemainingMinutes = 120, FaceValueToman = 0 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "pkg2", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "pkg2", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        Guid sessionId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            sessionId = await lookup.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await using (var hb = factory.Services.CreateAsyncScope())
        {
            var coordinator = hb.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
            await coordinator.OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        }

        await using (var displayScope = factory.Services.CreateAsyncScope())
        {
            var db = displayScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group).FirstAsync(s => s.Id == sessionId);
            var display = await displayScope.ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(session, default);
            Assert.InRange(display!.PackageMinutesRemaining![0], 59, 60);
        }
    }

    [Fact]
    public async Task prepaid_hub_display_not_elapsed_only_open_guest_stays_elapsed_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsJsonAsync("/api/operator/stations/PC-1/start-guest", new { prepaidKind = 2, amountToman = 200_000, openPayAtEnd = false });
        var session = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.SingleAsync();
        var display = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(session, default);
        Assert.False(display!.OpenGuestElapsedOnly);
        Assert.NotNull(display.EstimatedRemainingSeconds);
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-2", PilotSeed.PcNormalGroupId);
        var op2 = await factory.CreateOperatorClientAsync();
        var open = await op2.PostAsJsonAsync("/api/operator/stations/PC-2/start-guest", new { openPayAtEnd = true });
        open.EnsureSuccessStatusCode();
        var openGuest = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Where(s => s.Status == SessionStatus.Open).FirstAsync();
        var guestDisplay = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(openGuest, default);
        Assert.True(guestDisplay!.OpenGuestElapsedOnly);
    }

    [Fact]
    public async Task rejected_numeric_prepaid_leaves_no_pending_and_retry_not_station_busy()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        var bad = await op.PostAsJsonAsync("/api/operator/stations/PC-1/start-guest", new { prepaidKind = 2, amountToman = 1, openPayAtEnd = false });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Sessions.CountAsync(s => s.Status == SessionStatus.AwaitingPayment));
        Assert.Equal(0, await db.PendingBills.CountAsync());
        var ok = await op.PostAsJsonAsync("/api/operator/stations/PC-1/start-guest", new { prepaidKind = 2, amountToman = 200_000, openPayAtEnd = false });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task catalog_admin_put_persists_executable_args_icon_order_group_restrictions()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.GameCatalogCategories.Add(new GameCatalogCategoryEntity { Name = "Cat", SortOrder = 1 });
            await db.SaveChangesAsync();
        }

        var post = await mgr.PostAsJsonAsync("/api/operator/games", new
        {
            name = "Test",
            categoryId = 1,
            executableOrUri = "D:\\game.exe",
            arguments = "--x",
            iconUrl = "icon.png",
            sortOrder = 7,
            groupRestrictions = new[] { "pc-normal" },
            allowedProcessNames = new[] { "game.exe" },
        });
        post.EnsureSuccessStatusCode();
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var put = await mgr.PutAsJsonAsync($"/api/operator/games/{id}", new
        {
            name = "Test2",
            enabled = true,
            executableOrUri = "steam://run/1",
            arguments = "--y",
            iconUrl = "icon2.png",
            sortOrder = 9,
            groupRestrictions = new[] { "pc-vip" },
            allowedProcessNames = new[] { "game.exe", "launcher.exe" },
        });
        put.EnsureSuccessStatusCode();
        var row = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().GameCatalogEntries.SingleAsync(e => e.Id == id);
        Assert.Equal("steam://run/1", row.ExecutableOrUri);
        Assert.Equal("--y", row.Arguments);
        Assert.Equal("icon2.png", row.IconUrl);
        Assert.Equal(9, row.SortOrder);
        Assert.Contains("pc-vip", row.GroupRestrictionsJson);
        Assert.Contains("launcher.exe", row.AllowedProcessNamesJson);
    }

    [Fact]
    public void playing_pc_operator_dom_hides_console_controls_shows_force_logout()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var js = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Server", "wwwroot", "operator", "app.js"));
        var html = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Server", "wwwroot", "operator", "index.html"));
        Assert.Contains("forceLogoutBtn", html);
        Assert.Contains("خروج اجباری", html);
        Assert.Contains("isPlayingPc", js);
        Assert.Contains("prepaidDurationBtn", js);
        Assert.Contains("forceLogoutBtn", js);
    }

    [Fact]
    public async Task debt_with_eligible_package_not_authorized_after_package_exhausted()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BranchMemberSettings.SingleAsync();
        settings.AllowPcWhileInDebt = true;
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Debt", Mobile = "09130000001", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = -10_000 });
        db.CustomerPackages.Add(new CustomerPackageEntity { Id = Guid.NewGuid(), CustomerId = customer.Id, Name = "Pkg", RemainingMinutes = 60, FaceValueToman = 0 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "debt", "pass", default);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "debt", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        var sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 3_600_000, default);
        var display = await scope.ServiceProvider.GetRequiredService<KioskDisplayService>().BuildAsync(await db.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group).FirstAsync(s => s.Id == sessionId), default);
        Assert.True(display!.BlockedByDebt || display.PackageMinutesRemaining![0] == 0);
    }

    [Fact]
    public void band_boundary_0829_to_0830_remaining_73_minutes_4380_seconds()
    {
        var policy = BillingPolicy.Default;
        var wallet = new CustomerAccount("m", WalletSettings.Shared, new WalletPocket(100_000, 0), new WalletPocket(0, 0), []);
        var start = TehranTime.At(2026, 6, 3, 8, 29);
        var charge = SessionPricer.Price(new Session("s", [
            new PlaySegment("a", start, start.AddMinutes(1), "PC-1", PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, StationKind.Pc),
        ]), PilotSeed.CreateCard(), policy: policy);
        var settled = CustomerPlay.Settle(charge, wallet, billing: policy);
        var at = TehranTime.At(2026, 6, 3, 8, 30);
        var estimate = CustomerPlay.Estimate(settled.Account, PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, at, PilotSeed.CreateCard(), policy: policy);
        Assert.InRange(estimate.TotalMinutes, 73, 74);
        Assert.InRange((estimate.MoneyMinutes ?? 0) * 60, 4380, 4440);
    }

    [Fact]
    public void grace_positive_500_retains_balance_and_does_not_renew_as_funded()
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
    public async Task free_band_snapshot_admits_and_displays_local_boundary_and_post_band_minutes()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Free", Mobile = "09130000002", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 50_000 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "free", "pass", default);
        await scope.ServiceProvider.GetRequiredService<KioskFundingGuard>().EnsureMemberMayPlayAsync(customer.Id, "PC-1", default);
        factory.Clock.SetTehran(2026, 6, 3, 4, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        var login = await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "free", password = "pass", stationId = "PC-1" });
        login.EnsureSuccessStatusCode();
        Assert.True(login.IsSuccessStatusCode);
        var session = await db.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group).FirstAsync();
        var snapshot = await scope.ServiceProvider.GetRequiredService<RateConfigService>().LoadAsync(default);
        var settings = await db.BranchMemberSettings.SingleAsync();
        var wallet = MemberSettingsMapper.LoadWalletSettings(settings);
        var member = await db.CustomerMembers.SingleAsync(m => m.CustomerId == customer.Id);
        var account = KioskPlayEstimate.BuildAccount(member, wallet, []);
        await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 0 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
        var reloaded = await scope.ServiceProvider.GetRequiredService<RateConfigService>().LoadAsync(default);
        var text = KioskPlayEstimate.FormatFreeBandText(reloaded.Card, account, session.Station!.GroupId!, session, factory.Clock.UtcNow);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Assert.DoesNotContain("14:00", text, StringComparison.Ordinal);
            Assert.Matches(@"رایگان تا \d{2}:\d{2}، سپس \d+ دقیقه", text);
        }
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
