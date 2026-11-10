#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;
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
        await WaitForHeartbeatAdvanceAsync(_backend, deviceId, heartbeatBefore, lastSeenBefore);
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
            await _backend.BackdateOpenSessionForDeadPcSweepAsync(sessionId);
        }

        var sweep = await api.PostAsync("/api/internal/dead-pc-sweep", null);
        sweep.EnsureSuccessStatusCode();
        var closed = (await sweep.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("closed").GetInt32();
        Assert.Equal(1, closed);
        Assert.Equal(80_000, await _backend.PendingBillCollectibleAsync());
    }

    [Fact]
    public async Task live_server_z_disconnect_reconnect_same_session_no_double_debit()
    {
        OutOfProcessLiveServer? ownedServer = null;
        var priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var ciPid = 0;
        var usesCiServer = KioskIntegrationBackend.IsLiveMode
            && int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out ciPid);
        KioskIntegrationBackend backend;
        if (KioskIntegrationBackend.IsLiveMode)
        {
            Assert.True(usesCiServer, "GAMECLUB_SERVER_PID must be set on windows-2022 kiosk integration.");
            backend = KioskIntegrationBackend.Create(null);
            ownedServer = OutOfProcessLiveServer.AttachExisting(ciPid, priorCheckpointUrl!);
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")))
        {
            ownedServer = await OutOfProcessLiveServer.StartNewAsync();
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", ownedServer.BaseUrl);
            backend = KioskIntegrationBackend.CreateForBaseUrl(ownedServer.BaseUrl);
        }
        else
        {
            throw new InvalidOperationException("Out-of-process GameClub.Server with Postgres is required for reconnect proof.");
        }

        try
        {
            await backend.ResetAsync();
            await backend.RegisterPcAsync(backend.CreateApiClient(), "PC-DISC");
            var customerId = await backend.SeedCustomerWithCredentialAsync("disc", "pass", 200_000);
            var (deviceId, deviceKey, leasePem) = await backend.PairAsync("PC-DISC");
            var kioskHttp = backend.CreateKioskHttpClient(deviceId, deviceKey);
            var login = await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "disc", password = "pass", stationId = "PC-DISC" });
            login.EnsureSuccessStatusCode();
            var sessionId = await backend.OpenSessionIdAsync();
            var debitBefore = await backend.WalletDebitTotalAsync(customerId);

            var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-disc-{Guid.NewGuid():N}.db");
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.ConfigureLeaseVerifier(leasePem);
            var runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(
                backend.HubUri,
                deviceId,
                deviceKey,
                "PC-DISC",
                queuePath,
                legacyWindows: true,
                testHubHandler: backend.TestHubHandler);
            orchestrator.BindOfflineQueue(runtime.GetQueue());
            runtime.BindSession(sessionId);
            runtime.HeartbeatAcknowledged += ack =>
                orchestrator.OnHeartbeatAck(ack, sessionId, runtime.ClientMonotonicMs);
            runtime.HeartbeatTransportFailed += () =>
                orchestrator.EnqueuePoweredSegmentOnDisconnect(sessionId, runtime.ClientMonotonicMs);

            using var leaseCts = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                while (!leaseCts.IsCancellationRequested)
                {
                    orchestrator.TickLease(
                        sessionId,
                        runtime.ClientMonotonicMs,
                        DateTime.UtcNow,
                        transportDown: sessionId != Guid.Empty && !runtime.IsTransportConnected);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), leaseCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }, leaseCts.Token);

            await runtime.StartAsync(default);
            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));
            var heartbeatBefore = await backend.CountHeartbeatsAsync();
            await WaitForHeartbeatAdvanceAsync(backend, deviceId, heartbeatBefore, await backend.DeviceLastSeenUtcAsync(deviceId));

            if (usesCiServer)
            {
                Process.GetProcessById(ciPid).Kill(entireProcessTree: true);
            }
            else
            {
                ownedServer!.Stop();
            }

            var outageStart = DateTime.UtcNow;
            var sawDisconnected = false;
            while (DateTime.UtcNow - outageStart < TimeSpan.FromSeconds(20))
            {
                if (!runtime.IsTransportConnected)
                {
                    sawDisconnected = true;
                }

                if (sawDisconnected && DateTime.UtcNow - outageStart >= TimeSpan.FromSeconds(12))
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.True(sawDisconnected, "IsTransportConnected must be false during a real server outage.");
            var pending = await runtime.GetQueue().PendingAsync();
            Assert.Contains(pending, p => p.Type == "offline_play_segment");

            await ownedServer!.RelaunchAsync();

            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(60));
            Assert.True(runtime.IsTransportConnected, "IsTransportConnected must be true after the server restarts.");

            var drained = await orchestrator.DrainOfflineQueueAsync(async events =>
            {
                var payload = JsonSerializer.Serialize(events.Select(e => new KioskOutboundEvent
                {
                    EventId = e.EventId,
                    Type = e.Type,
                    PayloadJson = e.PayloadJson,
                    CreatedUtc = e.CreatedUtc,
                }));
                var response = await kioskHttp.PostAsync(
                    "/api/kiosk/devices/events",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<Guid>();
                }

                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!body.RootElement.TryGetProperty("ackedEventIds", out var ids))
                {
                    return Array.Empty<Guid>();
                }

                return ids.EnumerateArray().Select(e => e.GetGuid()).ToList();
            }, default);
            Assert.True(drained > 0, "Offline queue must drain through production /api/kiosk/devices/events after reconnect.");

            Assert.Equal(sessionId, await backend.OpenSessionIdAsync());
            Assert.Equal(debitBefore, await backend.WalletDebitTotalAsync(customerId));
            leaseCts.Cancel();
            await runtime.DisposeAsync();
        }
        finally
        {
            if (ownedServer is not null && usesCiServer)
            {
                try
                {
                    await ownedServer.RelaunchAsync();
                }
                catch
                {
                    // Best-effort restore for follow-on net48 tests.
                }
            }
            else if (ownedServer is not null)
            {
                await ownedServer.DisposeAsync();
                Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", priorCheckpointUrl);
            }
        }
    }

    private static async Task WaitUntilTransportConnectedAsync(KioskRuntime runtime, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (runtime.IsTransportConnected)
            {
                return;
            }

            await Task.Delay(500);
        }

        Assert.Fail("Kiosk runtime transport did not connect before timeout.");
    }

    private static async Task WaitForHeartbeatAdvanceAsync(KioskIntegrationBackend backend, Guid deviceId, int heartbeatBefore, DateTime? lastSeenBefore)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            var count = await backend.CountHeartbeatsAsync();
            var lastSeen = await backend.DeviceLastSeenUtcAsync(deviceId);
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
