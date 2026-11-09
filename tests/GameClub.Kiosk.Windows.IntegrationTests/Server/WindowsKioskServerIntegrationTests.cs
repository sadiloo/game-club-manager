#if NET10_0_OR_GREATER
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Server.Tests;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Kiosk runtime against in-process factory (Linux proof) or live server (Windows proof when CHECKPOINT_SERVER_URL is set).</summary>
[Collection("windows-kiosk-server")]
[Trait("Category", "WindowsServer")]
public sealed class WindowsKioskServerIntegrationTests
{
    private readonly KioskIntegrationBackend _backend;

    public WindowsKioskServerIntegrationTests(WindowsIntegrationApplicationFactory factory)
    {
        _backend = KioskIntegrationBackend.Create(factory);
    }

    [Fact]
    public async Task live_server_pair_f6_lock_and_heartbeat_rows()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-WIN");
        var customerId = await _backend.SeedCustomerWithCredentialAsync("winkiosk", "good-pass", 500_000);

        var (deviceId, deviceKey, _) = await _backend.PairAsync("PC-WIN");
        await _backend.AssertDevicePairedAsync(deviceId, "PC-WIN");
        var kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey);

        for (var i = 0; i < 5; i++)
        {
            var bad = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "wrong", stationId = "PC-WIN" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var locked = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "wrong", stationId = "PC-WIN" });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        await _backend.AssertCredentialLockedAsync(customerId, locked: true);

        var op = await _backend.CreateOperatorClientAsync();
        await op.PutAsJsonAsync($"/api/operator/customers/{customerId}/credential", new { loginName = "winkiosk", password = "good-pass" });
        var good = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "winkiosk", password = "good-pass", stationId = "PC-WIN" });
        good.EnsureSuccessStatusCode();
        var sessionId = (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

        var heartbeatBefore = await _backend.CountHeartbeatsAsync();
        var lastSeenBefore = await _backend.DeviceLastSeenUtcAsync(deviceId);

        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-win-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        runtime.Configure(
            _backend.HubUri,
            deviceId,
            deviceKey,
            "PC-WIN",
            queuePath,
            legacyWindows: true,
            testHubHandler: _backend.TestHubHandler);
        runtime.BindSession(sessionId);
        await runtime.StartAsync(default);
        await WaitForHeartbeatAdvanceAsync(deviceId, heartbeatBefore, lastSeenBefore);
        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task live_server_pairing_registers_device_on_station()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-PAIR");
        var (deviceId, _, _) = await _backend.PairAsync("PC-PAIR");
        await _backend.AssertDevicePairedAsync(deviceId, "PC-PAIR");
    }

    [Fact]
    public async Task live_server_dead_pc_sweep_after_checkpoint_gap()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-10M");
        var api = _backend.CreateApiClient();
        if (_backend.UsesMutableClock)
        {
            _backend.SetTehran(2026, 6, 3, 14, 0);
        }

        var sessionId = await _backend.StartSessionAsync("PC-10M");
        if (_backend.UsesMutableClock)
        {
            _backend.SetTehran(2026, 6, 3, 15, 0);
            await api.PostAsJsonAsync(
                $"/api/sessions/{sessionId}/checkpoints",
                new { at = S3TestHelpers.T(2026, 6, 3, 15, 0) });
            _backend.SetTehran(2026, 6, 3, 15, 10);
        }
        else
        {
            await api.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = DateTime.UtcNow });
            await _backend.BackdateOpenSessionForDeadPcSweepAsync(sessionId);
        }

        var sweep = await api.PostAsync("/api/internal/dead-pc-sweep", null);
        sweep.EnsureSuccessStatusCode();
        Assert.Equal(80_000, await _backend.PendingBillCollectibleAsync());
    }

    [Fact]
    public async Task live_server_disconnect_reconnect_same_session_no_double_debit()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-DISC");
        var customerId = await _backend.SeedCustomerWithCredentialAsync("disc", "pass", 200_000);

        var (deviceId, deviceKey, _) = await _backend.PairAsync("PC-DISC");
        var kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey);
        var login = await kioskHttp.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "disc", password = "pass", stationId = "PC-DISC" });
        login.EnsureSuccessStatusCode();
        var sessionId = await _backend.OpenSessionIdAsync();
        var debitBefore = await _backend.WalletDebitTotalAsync(customerId);

        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-disc-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        runtime.Configure(
            _backend.HubUri,
            deviceId,
            deviceKey,
            "PC-DISC",
            queuePath,
            legacyWindows: true,
            testHubHandler: _backend.TestHubHandler);
        await runtime.StartAsync(default);
        await runtime.DisposeAsync();

        var runtime2 = new KioskRuntime(() => kioskHttp);
        runtime2.Configure(
            _backend.HubUri,
            deviceId,
            deviceKey,
            "PC-DISC",
            queuePath,
            legacyWindows: true,
            testHubHandler: _backend.TestHubHandler);
        await runtime2.StartAsync(default);
        await runtime2.DisposeAsync();

        Assert.Equal(sessionId, await _backend.OpenSessionIdAsync());
        Assert.Equal(debitBefore, await _backend.WalletDebitTotalAsync(customerId));
    }

    private async Task WaitForHeartbeatAdvanceAsync(Guid deviceId, int heartbeatBefore, DateTime? lastSeenBefore)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            var count = await _backend.CountHeartbeatsAsync();
            var lastSeen = await _backend.DeviceLastSeenUtcAsync(deviceId);
            if (count < heartbeatBefore + 3)
            {
                continue;
            }

            if (lastSeenBefore is null || !lastSeen.HasValue)
            {
                return;
            }

            if (lastSeen > lastSeenBefore)
            {
                return;
            }
        }

        Assert.Fail("Expected at least 3 advancing kiosk heartbeats on the server.");
    }
}

public sealed class WindowsIntegrationApplicationFactory : GameClubWebApplicationFactory;

[CollectionDefinition("windows-kiosk-server")]
public sealed class WindowsKioskServerCollection : ICollectionFixture<WindowsIntegrationApplicationFactory>;

#endif
