#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using GameClub.Server.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Runs kiosk core runtime against MutableClock-hosted server (windows-2022 job).</summary>
[Collection("windows-kiosk-server")]
[Trait("Category", "WindowsServer")]
public sealed class WindowsKioskServerIntegrationTests
{
    private readonly GameClubWebApplicationFactory _factory;

    public WindowsKioskServerIntegrationTests(WindowsIntegrationApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task pair_login_heartbeat_and_f6_lock_against_live_factory_host()
    {
        await S3TestHelpers.ResetAsync(_factory);
        await S3TestHelpers.RegisterPc(_factory.CreateClientWithKey(), "PC-WIN", PilotSeed.PcNormalGroupId);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "WinKiosk", Mobile = "09128880001", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "winkiosk", "good-pass", default);
        }

        var (deviceId, deviceKey, _) = await PairAsync("PC-WIN");
        var kioskHttp = CreateKioskClient(deviceId, deviceKey);
        for (var i = 0; i < 5; i++)
        {
            var bad = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "wrong", stationId = "PC-WIN" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var locked = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "wrong", stationId = "PC-WIN" });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);

        var op = await _factory.CreateOperatorClientAsync();
        await op.PutAsJsonAsync($"/api/operator/customers/{(await FindCustomerIdAsync())}/credential", new { loginName = "winkiosk", password = "good-pass" });
        var good = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "good-pass", stationId = "PC-WIN" });
        good.EnsureSuccessStatusCode();

        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-win-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        var hubUri = new Uri(_factory.Server.BaseAddress!, "hubs/kiosk");
        runtime.Configure(hubUri, deviceId, deviceKey, "PC-WIN", queuePath, legacyWindows: true, testHubHandler: _factory.Server.CreateHandler());
        await runtime.StartAsync(default);
        Assert.True(runtime.IsTransportConnected);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task dead_pc_600_second_close_uses_controlled_clock_in_test_host()
    {
        await S3TestHelpers.ResetAsync(_factory);
        await S3TestHelpers.RegisterPc(_factory.CreateClientWithKey(), "PC-WIN", PilotSeed.PcNormalGroupId);
        _factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_factory.CreateClientWithKey(), "PC-WIN", 1);
        _factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _factory.CreateClientWithKey().PostAsJsonAsync(
            $"/api/sessions/{sessionId}/checkpoints",
            new { at = S3TestHelpers.T(2026, 6, 3, 15, 0) });
        _factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        await _factory.CreateClientWithKey().PostAsync("/api/internal/dead-pc-sweep", null);
        await using var scope = _factory.Services.CreateAsyncScope();
        var pending = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PendingBills.SingleAsync();
        Assert.Equal(80_000, pending.CollectibleToman);
    }

  [Fact]
    public async Task orchestrator_disconnect_and_reconnect_preserves_session()
    {
        await S3TestHelpers.ResetAsync(_factory);
        await S3TestHelpers.RegisterPc(_factory.CreateClientWithKey(), "PC-WIN", PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairAsync("PC-WIN");
        var kioskHttp = CreateKioskClient(deviceId, deviceKey);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Disc", Mobile = "09128880002", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 200_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "disc", "pass", default);
        }

        var login = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "disc", password = "pass", stationId = "PC-WIN" });
        login.EnsureSuccessStatusCode();
        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-disc-{Guid.NewGuid():N}.db");
        var hubHandler = _factory.Server.CreateHandler();
        var runtime = new KioskRuntime(() => kioskHttp);
        var hubUri = new Uri(_factory.Server.BaseAddress!, "hubs/kiosk");
        runtime.Configure(hubUri, deviceId, deviceKey, "PC-WIN", queuePath, legacyWindows: true, testHubHandler: hubHandler);
        await runtime.StartAsync(default);
        Assert.True(runtime.IsTransportConnected);
        await runtime.DisposeAsync();
        var runtime2 = new KioskRuntime(() => kioskHttp);
        runtime2.Configure(hubUri, deviceId, deviceKey, "PC-WIN", queuePath, legacyWindows: true, testHubHandler: hubHandler);
        await runtime2.StartAsync(default);
        Assert.True(runtime2.IsTransportConnected);
        await runtime2.DisposeAsync();
    }

    private async Task<Guid> FindCustomerIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.Select(c => c.Id).FirstAsync();
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairAsync(string stationId)
    {
        var op = await _factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = (await codeBody.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
        var client = _factory.CreateClient();
        var pair = await client.PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }
}

public sealed class WindowsIntegrationApplicationFactory : GameClubWebApplicationFactory;

[CollectionDefinition("windows-kiosk-server")]
public sealed class WindowsKioskServerCollection : ICollectionFixture<WindowsIntegrationApplicationFactory>;

#endif
