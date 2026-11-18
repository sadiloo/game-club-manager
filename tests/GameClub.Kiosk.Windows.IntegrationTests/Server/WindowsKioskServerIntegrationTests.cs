#if NET10_0_OR_GREATER
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Server.Kiosk;
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

    [Fact(Timeout = 180_000)]
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

    [Fact(Timeout = 180_000)]
    public async Task live_server_login_waits_for_transport_before_session_starts()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-TRANSPORT");
        await _backend.SeedCustomerWithCredentialAsync("tranlogin", "pass", 100_000);
        var (deviceId, deviceKey, _) = await _backend.PairAsync("PC-TRANSPORT");
        var kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey);
        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-transport-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        runtime.Configure(
            _backend.HubUri,
            deviceId,
            deviceKey,
            "PC-TRANSPORT",
            queuePath,
            legacyWindows: true,
            testHubHandler: _backend.TestHubHandler);
        await runtime.StartAsync(default);
        await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));
        var login = await kioskHttp.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "tranlogin", password = "pass", stationId = "PC-TRANSPORT" });
        login.EnsureSuccessStatusCode();
        runtime.BindSession((await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid());
        Assert.NotEqual(Guid.Empty, await _backend.OpenSessionIdAsync());
        await runtime.DisposeAsync();
    }

    [Fact(Timeout = 180_000)]
    public async Task live_server_kiosk_reconnects_when_server_unreachable_at_start()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMECLUB_DATABASE"))
            && !KioskIntegrationBackend.IsLiveMode)
        {
            throw new InvalidOperationException("Postgres or CHECKPOINT_SERVER_URL required for B-7(a) proof.");
        }

        OutOfProcessLiveServer? ownedServer = null;
        KioskIntegrationBackend backend;
        var priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var ciPid = 0;
        var usesCiServer = KioskIntegrationBackend.IsLiveMode
            && int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out ciPid);
        KioskRuntime? runtime = null;
        using var leaseCts = new CancellationTokenSource();
        var serverStoppedForOutage = false;
        var failedStarts = 0;
        KioskHeartbeatAck? lastAck = null;
        try
        {
            if (KioskIntegrationBackend.IsLiveMode)
            {
                Assert.True(usesCiServer, "GAMECLUB_SERVER_PID must be set on windows-2022 kiosk integration.");
                backend = KioskIntegrationBackend.Create(null);
                ownedServer = OutOfProcessLiveServer.AttachExisting(ciPid, priorCheckpointUrl!);
            }
            else
            {
                Environment.SetEnvironmentVariable("GAMECLUB_ALLOW_TEST_CLOCK", "1");
                ownedServer = await OutOfProcessLiveServer.StartNewAsync();
                Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", ownedServer.BaseUrl);
                backend = KioskIntegrationBackend.CreateForBaseUrl(ownedServer.BaseUrl);
            }

            await backend.ResetAsync();
            await backend.PinDaytimeTestClockAsync();
            await backend.SetPcNormalOfflineRatesAsync();
            await backend.RegisterPcAsync(backend.CreateApiClient(), "PC-UNREACH");
            await backend.SeedCustomerWithCredentialAsync("unreach", "pass", 200_000);
            var (deviceId, deviceKey, leasePem) = await backend.PairAsync("PC-UNREACH");
            var kioskHttp = backend.CreateKioskHttpClient(deviceId, deviceKey);
            var login = await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "unreach", password = "pass", stationId = "PC-UNREACH" });
            login.EnsureSuccessStatusCode();
            var sessionId = await backend.OpenSessionIdAsync();
            const long startWalletToman = 200_000;
            var sessionStartedUtc = DateTime.UtcNow;
            var rateCard = new RateCardBuilder().AddBase(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalHourly).Build();
            var fundedPlaySeconds = (int)TimeSpan.FromMinutes(
                RemainingTimeEstimator.Estimate(
                    new PcAccount("unreach", startWalletToman),
                    PilotSeed.PcNormalGroupId,
                    PlayMode.Offline,
                    1,
                    DateTime.UtcNow,
                    rateCard,
                    graceMinuteEnabled: true).MoneyMinutes!.Value).TotalSeconds;

            if (usesCiServer)
            {
                KioskLiveServerRegistry.LogServerKill(ciPid, "live_server_unreachable_ci_outage");
                KioskLiveServerRegistry.Unregister(ciPid);
                Process.GetProcessById(ciPid).Kill(entireProcessTree: true);
            }
            else
            {
                ownedServer!.Stop();
            }

            serverStoppedForOutage = true;

            var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-unreach-{Guid.NewGuid():N}.db");
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.ConfigureLeaseVerifier(leasePem);
            runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(
                backend.HubUri,
                deviceId,
                deviceKey,
                "PC-UNREACH",
                queuePath,
                legacyWindows: true,
                testHubHandler: backend.TestHubHandler);
            orchestrator.BindOfflineQueue(runtime.GetQueue());
            runtime.BindSession(sessionId);
            runtime.ConnectionStatusChanged += (phase, _) =>
            {
                if (phase == KioskTransportPhase.LastError)
                {
                    Interlocked.Increment(ref failedStarts);
                }
            };
            runtime.HeartbeatAcknowledged += ack =>
            {
                lastAck = ack;
                orchestrator.OnHeartbeatAck(ack, sessionId, runtime.ClientMonotonicMs);
            };
            runtime.HeartbeatTransportFailed += () =>
                orchestrator.EnqueuePoweredSegmentOnDisconnect(sessionId, runtime.ClientMonotonicMs);

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
            await Task.Delay(TimeSpan.FromSeconds(20));
            Assert.True(failedStarts >= 2, $"expected >=2 failed starts while server down, saw {failedStarts}");

            await ownedServer!.RelaunchAsync();
            serverStoppedForOutage = false;

            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(30));
            Assert.True(runtime.IsTransportConnected, "Transport must connect within 30 seconds after server returns.");

            var ackDeadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < ackDeadline)
            {
                if (lastAck?.Lease is not null
                    && lastAck.Display?.EstimatedRemainingSeconds is > 0
                    && orchestrator.Phase == KioskShellPhase.Playing)
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.NotNull(lastAck);
            Assert.NotNull(lastAck.Lease);
            // Upper bound = funded play seconds (RemainingTimeEstimator + RateCardBuilder on real wallet/rate) + 5 s one heartbeat tick.
            Assert.True(lastAck.Display!.EstimatedRemainingSeconds > 0);
            var elapsedSeconds = (int)(DateTime.UtcNow - sessionStartedUtc).TotalSeconds;
            Assert.True(lastAck.Display.EstimatedRemainingSeconds <= fundedPlaySeconds + 5);
            Assert.True(lastAck.Display.EstimatedRemainingSeconds >= fundedPlaySeconds - elapsedSeconds - 60);
            Assert.Equal(KioskShellPhase.Playing, orchestrator.Phase);

            var stop = await backend.CreateApiClient().PostAsync($"/api/sessions/{sessionId}/stop", null);
            stop.EnsureSuccessStatusCode();
            var lockDeadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < lockDeadline)
            {
                if (lastAck?.SessionStatus == "AwaitingPayment"
                    && lastAck.Lease is null
                    && orchestrator.Phase is KioskShellPhase.Ended or KioskShellPhase.Locked)
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.Equal("AwaitingPayment", lastAck?.SessionStatus);
            Assert.Null(lastAck?.Lease);
            Assert.True(orchestrator.Phase is KioskShellPhase.Ended or KioskShellPhase.Locked);

            await runtime.DisposeAsync();
            runtime = null;
        }
        finally
        {
            leaseCts.Cancel();
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }

            await FinalizeCiOwnedServerAsync(ownedServer, usesCiServer, serverStoppedForOutage, priorCheckpointUrl);
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task live_server_pairing_registers_device_on_station()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-PAIR");
        var (deviceId, _, _) = await _backend.PairAsync("PC-PAIR");
        await _backend.AssertDevicePairedAsync(deviceId, "PC-PAIR");
    }

    [Fact(Timeout = 180_000)]
    public async Task live_server_dead_pc_sweep_after_checkpoint_gap()
    {
        await _backend.ResetAsync();
        await _backend.PinDaytimeTestClockAsync();
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

    [Fact(Timeout = 360_000)]
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

        KioskRuntime? runtime = null;
        using var leaseCts = new CancellationTokenSource();
        var serverStoppedForOutage = false;
        try
        {
            await backend.ResetAsync();
            await backend.PinDaytimeTestClockAsync();
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
            runtime = new KioskRuntime(() => kioskHttp);
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
                KioskLiveServerRegistry.LogServerKill(ciPid, "live_server_z_disconnect_ci_outage");
                KioskLiveServerRegistry.Unregister(ciPid);
                Process.GetProcessById(ciPid).Kill(entireProcessTree: true);
            }
            else
            {
                ownedServer!.Stop();
            }

            serverStoppedForOutage = true;

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
            serverStoppedForOutage = false;

            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(60));
            Assert.True(runtime.IsTransportConnected, "IsTransportConnected must be true after the server restarts.");

            async Task<int> SubmitDrainAsync()
            {
                return await orchestrator.DrainOfflineQueueAsync(async events =>
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
            }

            var totalDrained = 0;
            var drainDeadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < drainDeadline)
            {
                totalDrained += await SubmitDrainAsync();
                var stillPending = await runtime.GetQueue().PendingAsync();
                if (!stillPending.Any(p => p.Type == "offline_play_segment"))
                {
                    break;
                }

                await Task.Delay(500);
            }

            Assert.True(totalDrained > 0, "Offline queue must drain through production /api/kiosk/devices/events after reconnect.");

            Assert.Equal(sessionId, await backend.OpenSessionIdAsync());
            Assert.Equal(debitBefore, await backend.WalletDebitTotalAsync(customerId));
        }
        finally
        {
            leaseCts.Cancel();
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }

            await FinalizeCiOwnedServerAsync(ownedServer, usesCiServer, serverStoppedForOutage, priorCheckpointUrl);
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task live_server_re_pair_new_device_key_heartbeats_resume()
    {
        await _backend.ResetAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-REKEY");
        var (deviceId, deviceKey1, leasePem) = await _backend.PairAsync("PC-REKEY");
        var kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey1);
        var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-rekey-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => kioskHttp);
        runtime.Configure(_backend.HubUri, deviceId, deviceKey1, "PC-REKEY", queuePath, legacyWindows: true, testHubHandler: _backend.TestHubHandler);
        var acks = 0;
        runtime.HeartbeatAcknowledged += _ => Interlocked.Increment(ref acks);
        await runtime.StartAsync(default);
        await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));
        await Task.Delay(TimeSpan.FromSeconds(8));
        var acksBefore = acks;
        Assert.True(acksBefore >= 1, "heartbeats must work before re-pair");

        var op = await _backend.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync("/api/operator/stations/PC-REKEY/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = (await codeBody.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString()!;
        var pair = await _backend.CreateApiClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var pairDoc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        var deviceKey2 = pairDoc.RootElement.GetProperty("deviceKey").GetString()!;
        Assert.Equal(deviceId, pairDoc.RootElement.GetProperty("deviceId").GetGuid());
        Assert.NotEqual(deviceKey1, deviceKey2);

        kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey2);
        await runtime.ResetForReconfigureAsync();
        runtime.Configure(_backend.HubUri, deviceId, deviceKey2, "PC-REKEY", queuePath, legacyWindows: true, testHubHandler: _backend.TestHubHandler);
        await runtime.StartAsync(default);
        await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && acks <= acksBefore)
        {
            await Task.Delay(500);
        }

        Assert.True(acks > acksBefore, "heartbeats must resume after re-pair with new device key");
        await runtime.DisposeAsync();
    }

    [SkippableFact(Timeout = 1_200_000)]
    public async Task live_server_two_outages_bill_matches_main_g2_oracle()
    {
        Skip.If(
            KioskIntegrationBackend.IsLiveMode,
            "G2 ~11 min owned-server oracle runs on ubuntu (dedicated CI step); Windows live job does not host a second server.");

        OutOfProcessLiveServer? ownedServer = null;
        var priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        try
        {
            Environment.SetEnvironmentVariable("GAMECLUB_ALLOW_TEST_CLOCK", "1");
            ownedServer = await OutOfProcessLiveServer.StartNewAsync();
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", ownedServer.BaseUrl);
            var backend = KioskIntegrationBackend.CreateForBaseUrl(ownedServer.BaseUrl);
            await backend.ResetAsync();
            await backend.PinDaytimeTestClockAsync();
            await backend.SetPcNormalOfflineRate40000Async();
            await backend.RegisterPcAsync(backend.CreateApiClient(), "PC-G2");
            var customerId = await backend.SeedCustomerWithCredentialAsync("g2user", "pass", 500_000);
            var (deviceId, deviceKey, leasePem) = await backend.PairAsync("PC-G2");
            var kioskHttp = backend.CreateKioskHttpClient(deviceId, deviceKey);
            var login = await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "g2user", password = "pass", stationId = "PC-G2" });
            login.EnsureSuccessStatusCode();
            var sessionId = await backend.OpenSessionIdAsync();
            var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-g2-{Guid.NewGuid():N}.db");
            var orchestrator = new KioskShellOrchestrator();
            orchestrator.ConfigureLeaseVerifier(leasePem);
            var runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(backend.HubUri, deviceId, deviceKey, "PC-G2", queuePath, legacyWindows: true, testHubHandler: backend.TestHubHandler);
            orchestrator.BindOfflineQueue(runtime.GetQueue());
            runtime.BindSession(sessionId);
            runtime.HeartbeatAcknowledged += ack =>
            {
                orchestrator.OnHeartbeatAck(ack, sessionId, runtime.ClientMonotonicMs);
                _ = DrainG2QueueOnAckAsync(orchestrator, kioskHttp, runtime);
            };
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
                        transportDown: !runtime.IsTransportConnected);
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
            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(60));
            var poweredSpanStartUtc = DateTime.UtcNow;
            await Task.Delay(TimeSpan.FromMinutes(6));

            ownedServer.Stop();
            await Task.Delay(TimeSpan.FromSeconds(120));
            await ownedServer.RelaunchAsync();
            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(180));
            await Task.Delay(TimeSpan.FromSeconds(90));

            var afterOutage1 = (await runtime.GetQueue().PendingAsync()).Count(e => e.Type == "offline_play_segment");

            ownedServer.Stop();
            await Task.Delay(TimeSpan.FromSeconds(120));
            var afterOutage2 = (await runtime.GetQueue().PendingAsync()).Count(e => e.Type == "offline_play_segment");
            Assert.True(afterOutage2 > afterOutage1, "outage 2 must enqueue offline_play_segment evidence");

            var poweredSpanSeconds = (DateTime.UtcNow - poweredSpanStartUtc).TotalSeconds;
            var secondsPastMinute = poweredSpanSeconds % 60;
            var marginToNearestMinute = Math.Min(secondsPastMinute, secondsPastMinute == 0 ? 60 : 60 - secondsPastMinute);
            Console.WriteLine(
                $"G2_MINUTE_BOUNDARY_MARGIN measured_seconds={poweredSpanSeconds:F3} margin_s={marginToNearestMinute:F3}");
            if (marginToNearestMinute < 10)
            {
                throw new InvalidOperationException(
                    $"INCONCLUSIVE: G2 span margin to nearest minute boundary is {marginToNearestMinute:F1}s (need >= 10s)");
            }

            leaseCts.Cancel();
            await runtime.DisposeAsync();
            await ownedServer.RelaunchAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));

            var queueOnly = new SqliteOfflineEventQueue(queuePath);
            orchestrator.BindOfflineQueue(queueOnly);
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
                return body.RootElement.TryGetProperty("ackedEventIds", out var ids)
                    ? ids.EnumerateArray().Select(x => x.GetGuid()).ToList()
                    : (IReadOnlyList<Guid>)Array.Empty<Guid>();
            }, default);
            Assert.True(drained > 0);

            var stop = await backend.CreateApiClient().PostAsync($"/api/sessions/{sessionId}/stop", null);
            stop.EnsureSuccessStatusCode();
            var evidence = await backend.CollectG2EvidenceAsync(sessionId, poweredSpanSeconds);
            Console.WriteLine("G2_ROW_DUMP " + evidence.ToDumpLine());
            Console.WriteLine(
                $"G2_ORACLE measured_seconds={poweredSpanSeconds:F1} minutes={evidence.ExpectedBillableMinutes} expected_collectible={evidence.ExpectedCollectibleToman}");
            Assert.Equal(40_000, evidence.PcNormalOfflineHourlyToman);
            Assert.Equal(evidence.ExpectedBillableMinutes, evidence.CheckpointCount);
            Assert.Equal(0, evidence.DuplicateCheckpointMinuteBuckets);
            Assert.Equal(evidence.ExpectedCollectibleToman, evidence.PendingBillCollectibleToman);
        }
        finally
        {
            if (ownedServer is not null)
            {
                await ownedServer.DisposeAsync();
                Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", priorCheckpointUrl);
            }
        }
    }

    private static async Task DrainG2QueueOnAckAsync(
        KioskShellOrchestrator orchestrator,
        HttpClient kioskHttp,
        KioskRuntime runtime)
    {
        if (!runtime.IsTransportConnected)
        {
            return;
        }

        try
        {
            await orchestrator.DrainOfflineQueueAsync(async events =>
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
                return body.RootElement.TryGetProperty("ackedEventIds", out var ids)
                    ? ids.EnumerateArray().Select(x => x.GetGuid()).ToList()
                    : (IReadOnlyList<Guid>)Array.Empty<Guid>();
            }, default);
        }
        catch
        {
            // drain is best-effort on ack, same as QA harness
        }
    }

    private static async Task FinalizeCiOwnedServerAsync(
        OutOfProcessLiveServer? ownedServer,
        bool usesCiServer,
        bool serverStoppedForOutage,
        string? priorCheckpointUrl)
    {
        if (ownedServer is null)
        {
            return;
        }

        if (!usesCiServer)
        {
            await ownedServer.DisposeAsync();
            Environment.SetEnvironmentVariable("CHECKPOINT_SERVER_URL", priorCheckpointUrl);
            return;
        }

        if (serverStoppedForOutage)
        {
            try
            {
                await ownedServer.RelaunchAsync();
            }
            catch
            {
                return;
            }
        }

        ownedServer.RecordCiLiveServerPidForIntegrationCleanup();
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

    /// <summary>Brief §10.4.7 (b) — server outage while internet OFF; station desired stays OFF (L3 firewall proof in net48 guard tests).</summary>
    [Fact(Timeout = 180_000)]
    public async Task live_server_pc_internet_off_unaffected_by_server_outage()
    {
        if (!KioskIntegrationBackend.IsLiveMode)
        {
            return;
        }

        OutOfProcessLiveServer? ownedServer = null;
        var priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var ciPid = 0;
        var usesCiServer = KioskIntegrationBackend.IsLiveMode
            && int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out ciPid);
        var serverStopped = false;
        try
        {
            var backend = KioskIntegrationBackend.Create(null);
            ownedServer = OutOfProcessLiveServer.AttachExisting(ciPid, priorCheckpointUrl!);
            await backend.ResetAsync();
            await backend.PinDaytimeTestClockAsync();
            await backend.SetPcNormalOfflineRatesAsync();
            await backend.RegisterPcAsync(backend.CreateApiClient(), "PC-OUTAGE");
            await backend.SeedCustomerWithCredentialAsync("inetout", "pass", 200_000);
            var (deviceId, deviceKey, _) = await backend.PairAsync("PC-OUTAGE");
            var kioskHttp = backend.CreateKioskHttpClient(deviceId, deviceKey);
            (await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "inetout", password = "pass", stationId = "PC-OUTAGE" })).EnsureSuccessStatusCode();
            var sessionId = await backend.OpenSessionIdAsync();
            var op = await backend.CreateOperatorClientAsync();
            var rv = (await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid"))!
                .Single(t => t.GetProperty("id").GetString() == "PC-OUTAGE").GetProperty("rowVersion").GetUInt32();
            (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
            {
                on = false,
                expectedVersion = rv,
                commandId = Guid.NewGuid(),
            })).EnsureSuccessStatusCode();

            KioskLiveServerRegistry.LogServerKill(ciPid, "live_server_pc_internet_off_outage_b");
            KioskLiveServerRegistry.Unregister(ciPid);
            Process.GetProcessById(ciPid).Kill(entireProcessTree: true);
            serverStopped = true;

            await ownedServer.RelaunchAsync();
            serverStopped = false;

            op = await backend.CreateOperatorClientAsync();
            var tile = (await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid"))!
                .Single(t => t.GetProperty("id").GetString() == "PC-OUTAGE");
            Assert.False(tile.GetProperty("internetOn").GetBoolean());
            Assert.False(tile.GetProperty("internetDesiredOn").GetBoolean());
            _ = deviceKey;
            _ = sessionId;
        }
        finally
        {
            if (usesCiServer && ownedServer is not null && serverStopped)
            {
                try
                {
                    await ownedServer.RelaunchAsync();
                }
                catch
                {
                    /* best-effort */
                }
            }
            else if (usesCiServer && ownedServer is not null)
            {
                await FinalizeCiOwnedServerAsync(ownedServer, usesCiServer, false, priorCheckpointUrl);
            }
        }
    }

    /// <summary>Brief §10.4.7 (c) — kiosk killed while disconnected; ON press queues until reconnect, segment at press.</summary>
    [Fact(Timeout = 240_000)]
    public async Task live_server_pc_internet_on_while_kiosk_killed_shows_pending_then_applied_on_reconnect()
    {
        if (!KioskIntegrationBackend.IsLiveMode)
        {
            return;
        }

        OutOfProcessLiveServer? ownedServer = null;
        var priorCheckpointUrl = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
        var ciPid = 0;
        var usesCiServer = KioskIntegrationBackend.IsLiveMode
            && int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out ciPid);
        KioskRuntime? runtime = null;
        var serverStopped = false;
        try
        {
            var backend = KioskIntegrationBackend.Create(null);
            ownedServer = OutOfProcessLiveServer.AttachExisting(ciPid, priorCheckpointUrl!);
            await backend.ResetAsync();
            await backend.PinDaytimeTestClockAsync();
            await backend.SetPcNormalOfflineRatesAsync();
            await backend.RegisterPcAsync(backend.CreateApiClient(), "PC-KILL");
            await backend.SeedCustomerWithCredentialAsync("inetkill", "pass", 200_000);
            var (deviceId, deviceKey, _) = await backend.PairAsync("PC-KILL");
            var kioskHttp = backend.CreateKioskHttpClient(deviceId, deviceKey);
            var login = await kioskHttp.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "inetkill", password = "pass", stationId = "PC-KILL" });
            login.EnsureSuccessStatusCode();
            var sessionId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

            var queuePath = Path.Combine(Path.GetTempPath(), $"kiosk-inet-kill-{Guid.NewGuid():N}.db");
            runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(
                backend.HubUri,
                deviceId,
                deviceKey,
                "PC-KILL",
                queuePath,
                legacyWindows: true,
                testHubHandler: backend.TestHubHandler);
            runtime.BindSession(sessionId);
            await runtime.StartAsync(default);
            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));

            var op = await backend.CreateOperatorClientAsync();
            var rv0 = (await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid"))!
                .Single(t => t.GetProperty("id").GetString() == "PC-KILL").GetProperty("rowVersion").GetUInt32();
            (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
            {
                on = false,
                expectedVersion = rv0,
                commandId = Guid.NewGuid(),
            })).EnsureSuccessStatusCode();

            KioskLiveServerRegistry.LogServerKill(ciPid, "live_server_pc_internet_kiosk_kill_c");
            KioskLiveServerRegistry.Unregister(ciPid);
            Process.GetProcessById(ciPid).Kill(entireProcessTree: true);
            serverStopped = true;
            await runtime.DisposeAsync();
            runtime = null;

            await ownedServer.RelaunchAsync();
            serverStopped = false;
            await Task.Delay(TimeSpan.FromSeconds(3));

            op = await backend.CreateOperatorClientAsync();
            var tile = (await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid"))!
                .Single(t => t.GetProperty("id").GetString() == "PC-KILL");
            var rv = tile.GetProperty("rowVersion").GetUInt32();
            var pressAt = DateTime.UtcNow;
            (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
            {
                on = true,
                expectedVersion = rv,
                commandId = Guid.NewGuid(),
            })).EnsureSuccessStatusCode();

            var afterPress = (await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid"))!
                .Single(t => t.GetProperty("id").GetString() == "PC-KILL");
            Assert.True(afterPress.GetProperty("internetPending").GetBoolean());
            Assert.False(afterPress.GetProperty("kioskConnected").GetBoolean());
            Assert.True(afterPress.GetProperty("internetOn").GetBoolean());

            var segmentsBeforeReconnect = await backend.ReadPlaySegmentsAsync(sessionId);
            Assert.Contains(
                segmentsBeforeReconnect,
                s => string.Equals(s.Mode, nameof(PlayMode.Online), StringComparison.Ordinal));

            runtime = new KioskRuntime(() => kioskHttp);
            runtime.Configure(
                backend.HubUri,
                deviceId,
                deviceKey,
                "PC-KILL",
                queuePath,
                legacyWindows: true,
                testHubHandler: backend.TestHubHandler);
            runtime.BindSession(sessionId);
            await runtime.StartAsync(default);
            await WaitUntilTransportConnectedAsync(runtime, TimeSpan.FromSeconds(45));

            var appliedDeadline = DateTime.UtcNow.AddSeconds(45);
            JsonElement? lastTile = null;
            while (DateTime.UtcNow < appliedDeadline)
            {
                var grid = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
                lastTile = grid!.Single(t => t.GetProperty("id").GetString() == "PC-KILL");
                if (!lastTile.Value.GetProperty("internetPending").GetBoolean()
                    && lastTile.Value.GetProperty("internetOn").GetBoolean())
                {
                    break;
                }

                await Task.Delay(1_000);
            }

            Assert.NotNull(lastTile);
            Assert.True(lastTile!.Value.GetProperty("internetOn").GetBoolean());
            var onLineStart = segmentsBeforeReconnect
                .Single(s => string.Equals(s.Mode, nameof(PlayMode.Online), StringComparison.Ordinal))
                .StartUtc.ToUniversalTime();
            Assert.True(onLineStart >= pressAt.AddSeconds(-5), "ON segment must start at toggle press time.");
            _ = deviceKey;
        }
        finally
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }

            if (usesCiServer && ownedServer is not null && serverStopped)
            {
                try
                {
                    await ownedServer.RelaunchAsync();
                }
                catch
                {
                    /* best-effort */
                }
            }
            else if (usesCiServer && ownedServer is not null)
            {
                await FinalizeCiOwnedServerAsync(ownedServer, usesCiServer, false, priorCheckpointUrl);
            }
        }
    }

    /// <summary>Brief §10.4.7 (a),(d),(e) — live server internet desired/applied without cafe hardware.</summary>
    [Fact(Timeout = 120_000)]
    public async Task live_server_pc_internet_toggle_off_report_applied_then_session_end_on()
    {
        if (!KioskIntegrationBackend.IsLiveMode)
        {
            return;
        }

        await _backend.ResetAsync();
        await _backend.PinDaytimeTestClockAsync();
        await _backend.SetPcNormalOfflineRatesAsync();
        await _backend.RegisterPcAsync(_backend.CreateApiClient(), "PC-INET");
        await _backend.SeedCustomerWithCredentialAsync("inete2e", "pass", 200_000);
        var (deviceId, deviceKey, _) = await _backend.PairAsync("PC-INET");
        var kioskHttp = _backend.CreateKioskHttpClient(deviceId, deviceKey);
        var login = await kioskHttp.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "inete2e", password = "pass", stationId = "PC-INET" });
        login.EnsureSuccessStatusCode();
        var sessionId = await _backend.OpenSessionIdAsync();
        var op = await _backend.CreateOperatorClientAsync();
        var startGrid = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var rv = startGrid!.Single(t => t.GetProperty("id").GetString() == "PC-INET").GetProperty("rowVersion").GetUInt32();
        var off = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = rv,
            commandId = Guid.NewGuid(),
        });
        off.EnsureSuccessStatusCode();
        var offBody = await off.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(offBody!.GetProperty("internetOn").GetBoolean());
        var gridAfterOff = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tileAfterOff = gridAfterOff!.Single(t => t.GetProperty("id").GetString() == "PC-INET");
        Assert.True(tileAfterOff.GetProperty("internetPending").GetBoolean() || !tileAfterOff.GetProperty("internetOn").GetBoolean());

        var stop = await _backend.CreateApiClient().PostAsync($"/api/sessions/{sessionId}/stop", null);
        stop.EnsureSuccessStatusCode();
        var endGrid = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var endTile = endGrid!.Single(t => t.GetProperty("id").GetString() == "PC-INET");
        Assert.True(endTile.GetProperty("internetDesiredOn").GetBoolean());
    }
}

public sealed class WindowsIntegrationApplicationFactory : GameClubWebApplicationFactory;

[CollectionDefinition("windows-kiosk-server")]
public sealed class WindowsKioskServerCollection
    : ICollectionFixture<WindowsIntegrationApplicationFactory>, ICollectionFixture<KioskLiveServerAssemblyFixture>;

#endif
