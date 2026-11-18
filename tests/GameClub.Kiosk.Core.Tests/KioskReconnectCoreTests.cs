using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Tests.Fakes;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskReconnectCoreTests
{
    private sealed class RecordingQueue : IOfflineEventQueue
    {
        public List<string> Types { get; } = new();

        public Task EnqueueAsync(Guid eventId, string type, string payloadJson, DateTime createdUtc, CancellationToken ct = default)
        {
            lock (Types)
            {
                Types.Add(type);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<QueuedEvent>> PendingAsync(CancellationToken ct = default)
        {
            lock (Types)
            {
                return Task.FromResult<IReadOnlyList<QueuedEvent>>(Types.Select(t =>
                    new QueuedEvent(Guid.NewGuid(), t, "{}", DateTime.UtcNow)).ToList());
            }
        }

        public Task AckAsync(Guid eventId, CancellationToken ct = default) => Task.CompletedTask;

        public void Close()
        {
        }
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }
    }
    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 4)]
    [InlineData(2, 8)]
    [InlineData(3, 16)]
    [InlineData(4, 30)]
    [InlineData(5, 30)]
    public void reconnect_backoff_delay_stays_within_2_to_30_seconds(int attempt, int expectedSeconds)
    {
        var delay = KioskReconnectBackoff.DelayForAttempt(attempt);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task hub_lifecycle_retries_start_after_negotiate_failure()
    {
        var transport = new FakeKioskHubTransport
        {
            StartHandler = (attempt, _) =>
            {
                if (attempt < 2)
                {
                    throw new HttpRequestException("negotiate failed");
                }

                return Task.CompletedTask;
            },
        };
        var delays = new List<TimeSpan>();
        var runtime = new KioskRuntime(() => new HttpClient());
        runtime.TestDelayAsync = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        runtime.ConfigureForTests(transport, new ThrowingOfflineEventQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-1");
        KioskTransportPhase? lastError = null;
        runtime.ConnectionStatusChanged += (phase, _) =>
        {
            if (phase == KioskTransportPhase.LastError)
            {
                lastError = phase;
            }
        };

        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);

        Assert.True(lastError.HasValue);
        Assert.True(delays.Count >= 1);
        Assert.Equal(2, transport.StartCallCount);
    }

    [Fact]
    public async Task hub_lifecycle_restarts_on_closed_after_automatic_reconnect()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestDelayAsync = (_, _) => Task.CompletedTask,
        };
        runtime.ConfigureForTests(transport, new ThrowingOfflineEventQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-2");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(1, transport.StartCallCount);

        transport.ForceClose(new Exception("automatic reconnect exhausted"));
        await Task.Delay(200);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(2, transport.StartCallCount);
    }

    [Fact]
    public async Task heartbeat_survives_throwing_enqueue_async()
    {
        var transport = new FakeKioskHubTransport();
        var queue = new ThrowingOfflineEventQueue("heartbeat_error");
        var runtime = new KioskRuntime(() => new HttpClient());
        runtime.ConfigureForTests(transport, queue, "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-3");
        transport.HeartbeatHandler = _ => throw new InvalidOperationException("heartbeat failed");
        runtime.TestHeartbeatInterval = TimeSpan.FromMilliseconds(200);
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);

        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < deadline && transport.HeartbeatCallCount < 2)
        {
            await Task.Delay(100);
        }

        Assert.True(queue.EnqueueCallCount >= 1);
        Assert.True(transport.HeartbeatCallCount >= 2, "Heartbeat must be invoked again after enqueue threw.");
    }

    [Fact]
    public async Task heartbeat_supervisor_restarts_when_loop_task_fails()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestSingleHeartbeatIteration = true,
        };
        runtime.ConfigureForTests(transport, new ThrowingOfflineEventQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-4");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && runtime.HeartbeatLoopStartCount < 2)
        {
            await Task.Delay(50);
        }

        Assert.True(runtime.HeartbeatLoopStartCount >= 2);
    }

    [Fact]
    public async Task connection_status_not_connected_while_hub_disconnected()
    {
        var transport = new FakeKioskHubTransport();
        var reconnectBlocked = new TaskCompletionSource<bool>();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestDelayAsync = (_, _) => reconnectBlocked.Task,
        };
        runtime.ConfigureForTests(transport, new ThrowingOfflineEventQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-5");
        KioskTransportPhase lastPhase = KioskTransportPhase.Unconfigured;
        runtime.ConnectionStatusChanged += (phase, _) => lastPhase = phase;
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(KioskTransportPhase.Connected, lastPhase);

        transport.ForceClose();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && runtime.TransportPhase == KioskTransportPhase.Connected)
        {
            await Task.Delay(50);
        }

        Assert.NotEqual(KioskTransportPhase.Connected, runtime.TransportPhase);
        Assert.NotEqual(KioskTransportPhase.Connected, lastPhase);
        reconnectBlocked.TrySetResult(true);
    }

    [Fact]
    public async Task client_monotonic_increases_across_two_lifecycle_reconnects()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-mono");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await Task.Delay(1500);
        var before = runtime.ClientMonotonicMs;
        transport.ForceClose(new Exception("auto reconnect exhausted"));
        await WaitUntil(() => transport.StartCallCount >= 2 && runtime.IsTransportConnected, 5000);
        var mid = runtime.ClientMonotonicMs;
        Assert.True(mid >= before, $"first reconnect lowered mono: before={before} mid={mid}");
        transport.ForceClose(new Exception("auto reconnect exhausted"));
        await WaitUntil(() => transport.StartCallCount >= 3 && runtime.IsTransportConnected, 5000);
        var after = runtime.ClientMonotonicMs;
        Assert.True(after >= mid, $"second reconnect lowered mono: mid={mid} after={after}");
    }

    [Fact]
    public async Task offline_play_segment_still_enqueued_after_second_outage_in_same_session()
    {
        var transport = new FakeKioskHubTransport();
        var queue = new RecordingQueue();
        var sessionId = Guid.NewGuid();
        var orchestrator = new KioskShellOrchestrator();
        orchestrator.BindOfflineQueue(queue);
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport, queue, "http://x", Guid.NewGuid(), "k", "PC-out2");
        runtime.BindSession(sessionId);
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await Task.Delay(2000);
        var monoConnected = runtime.ClientMonotonicMs;
        Assert.True(monoConnected >= 1000);
        transport.ForceClose(new Exception("outage1"));
        await WaitUntil(() => !runtime.IsTransportConnected, 5000);
        orchestrator.EnqueuePoweredSegmentOnDisconnect(sessionId, 120_000);
        Assert.Equal(1, queue.Types.Count(t => t == "offline_play_segment"));
        await WaitUntil(() => runtime.IsTransportConnected, 5000);
        Assert.True(runtime.ClientMonotonicMs >= monoConnected, "monotonic must not reset after lifecycle reconnect");
        transport.ForceClose(new Exception("outage2"));
        await WaitUntil(() => !runtime.IsTransportConnected, 5000);
        orchestrator.EnqueuePoweredSegmentOnDisconnect(sessionId, 150_000);
        Assert.Equal(2, queue.Types.Count(t => t == "offline_play_segment"));
    }

    [Fact]
    public async Task re_pair_does_not_reset_client_monotonic_clock()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-mono-re");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await Task.Delay(800);
        var beforeSecondStart = runtime.ClientMonotonicMs;
        await runtime.ResetForReconfigureAsync();
        runtime.ConfigureForTests(new FakeKioskHubTransport(), new RecordingQueue(), "http://x2", Guid.NewGuid(), "k2", "PC-mono-re");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        Assert.True(runtime.ClientMonotonicMs >= beforeSecondStart, "re-pair StartAsync must not restart monotonic");
    }

    [Fact]
    public async Task re_pair_second_start_still_reconnects_after_transport_drop()
    {
        var deviceA = Guid.NewGuid();
        var deviceB = Guid.NewGuid();
        var transport1 = new FakeKioskHubTransport();
        var transport2 = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport1, new RecordingQueue(), "http://x/pair-a", deviceA, "key-a", "PC-repair");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await runtime.ResetForReconfigureAsync();
        runtime.ConfigureForTests(transport2, new RecordingQueue(), "http://x/pair-b", deviceB, "key-b", "PC-repair");
        await runtime.StartAsync(default);
        Assert.True(transport1.IsDisposed, "old transport must be disposed on re-pair");
        Assert.Equal(deviceB, runtime.DeviceId);
        Assert.Equal("http://x/pair-b", runtime.ServerBase);
        transport2.ForceClose(new Exception("auto reconnect exhausted"));
        await WaitUntil(() => transport2.StartCallCount >= 2 && runtime.IsTransportConnected, 5000);
        Assert.True(transport2.StartCallCount >= 2, $"StartCallCount={transport2.StartCallCount}");
        Assert.True(runtime.IsTransportConnected);
    }

    [Fact]
    public async Task re_pair_second_start_survives_two_minute_outage()
    {
        var serverUp = true;
        var transport1 = new FakeKioskHubTransport
        {
            StartHandler = (_, _) => serverUp ? Task.CompletedTask : throw new HttpRequestException("down"),
        };
        var transport2 = new FakeKioskHubTransport
        {
            StartHandler = (_, _) => serverUp ? Task.CompletedTask : throw new HttpRequestException("down"),
        };
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, ct) => Task.Delay(20, ct) };
        runtime.ConfigureForTests(transport1, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-F");
        await runtime.StartAsync(default);
        serverUp = false;
        await Task.Delay(3000);
        await runtime.ResetForReconfigureAsync();
        runtime.ConfigureForTests(transport2, new RecordingQueue(), "http://x", Guid.NewGuid(), "k2", "PC-F");
        await runtime.StartAsync(default);
        await Task.Delay(5000);
        serverUp = true;
        await WaitUntil(() => runtime.IsTransportConnected, 15_000);
        Assert.True(runtime.IsTransportConnected, $"never reconnected; starts={transport2.StartCallCount}");
    }

    [Fact]
    public async Task re_pair_during_backoff_connects_when_server_returns()
    {
        var serverUp = false;
        var deviceA = Guid.NewGuid();
        var transport1 = new FakeKioskHubTransport
        {
            StartHandler = (_, _) => serverUp ? Task.CompletedTask : throw new HttpRequestException("down"),
        };
        var transport2 = new FakeKioskHubTransport
        {
            StartHandler = (_, _) => serverUp ? Task.CompletedTask : throw new HttpRequestException("down"),
        };
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, ct) => Task.Delay(20, ct) };
        runtime.ConfigureForTests(transport1, new RecordingQueue(), "http://down", deviceA, "k", "PC-repair-b");
        await runtime.StartAsync(default);
        await WaitUntil(() => transport1.StartCallCount >= 2, 5000);
        await runtime.ResetForReconfigureAsync();
        runtime.ConfigureForTests(transport2, new RecordingQueue(), "http://up", Guid.NewGuid(), "k2", "PC-repair-b");
        await runtime.StartAsync(default);
        serverUp = true;
        await WaitUntil(() => runtime.IsTransportConnected, 5000);
        Assert.True(runtime.IsTransportConnected, $"never reconnected; starts={transport2.StartCallCount}");
        Assert.True(transport1.IsDisposed);
    }

    [Fact]
    public async Task hub_lifecycle_recovers_after_more_than_four_consecutive_start_failures_then_resets_backoff()
    {
        var failUntil = 10;
        var transport = new FakeKioskHubTransport
        {
            StartHandler = (n, _) => n <= failUntil ? throw new HttpRequestException("down " + n) : Task.CompletedTask,
        };
        var delays = new List<TimeSpan>();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestDelayAsync = (d, _) =>
            {
                lock (delays)
                {
                    delays.Add(d);
                }

                return Task.CompletedTask;
            },
        };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-P4");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        Assert.Equal(failUntil + 1, transport.StartCallCount);
        int[] expected;
        lock (delays)
        {
            expected = delays.Take(failUntil).Select(d => (int)d.TotalSeconds).ToArray();
        }

        Assert.Equal(new[] { 2, 4, 8, 16, 30, 30, 30, 30, 30, 30 }, expected);
        transport.ForceClose(new Exception("closed"));
        await WaitUntil(() => transport.StartCallCount >= failUntil + 2 && runtime.IsTransportConnected, 5000);
        int lastDelay;
        lock (delays)
        {
            lastDelay = (int)delays[^1].TotalSeconds;
        }

        Assert.Equal(2, lastDelay);
    }

    [Fact]
    public async Task hub_lifecycle_handles_multiple_closed_events_and_restarts()
    {
        var postCloseFailures = 0;
        var transport = new FakeKioskHubTransport
        {
            StartHandler = (n, _) =>
            {
                if (postCloseFailures > 0)
                {
                    postCloseFailures--;
                    throw new HttpRequestException("down");
                }

                return Task.CompletedTask;
            },
        };
        var delays = new List<TimeSpan>();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestDelayAsync = (d, _) =>
            {
                lock (delays)
                {
                    delays.Add(d);
                }

                return Task.CompletedTask;
            },
        };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-multi");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);

        postCloseFailures = 6;
        transport.ForceClose(new Exception("close1"));
        await WaitUntil(() => transport.StartCallCount >= 2 && runtime.IsTransportConnected, 5000);
        transport.ForceClose(new Exception("close2"));
        await WaitUntil(() => transport.StartCallCount >= 3 && runtime.IsTransportConnected, 5000);
        transport.ForceClose(new Exception("close3"));
        await WaitUntil(() => transport.StartCallCount >= 4 && runtime.IsTransportConnected, 8000);
        Assert.True(transport.StartCallCount >= 4);
        Assert.True(runtime.IsTransportConnected);
    }

    [Fact]
    public async Task heartbeat_inner_enqueue_catch_keeps_loop_without_supervisor_restart()
    {
        var transport = new FakeKioskHubTransport();
        var queue = new ThrowingOfflineEventQueue("heartbeat_error");
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestDisableHeartbeatSupervisorRestart = true,
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(200),
        };
        transport.HeartbeatHandler = _ => throw new InvalidOperationException("heartbeat failed");
        runtime.ConfigureForTests(transport, queue, "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-Mb");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && transport.HeartbeatCallCount < 2)
        {
            await Task.Delay(100);
        }

        Assert.True(transport.HeartbeatCallCount >= 2, "inner enqueue catch must keep heartbeat loop alive without supervisor restart");
    }

    [Fact]
    public async Task connection_status_subscriber_throw_does_not_stop_reconnect_loop()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-P5");
        var throwOnce = true;
        runtime.ConnectionStatusChanged += (phase, _) =>
        {
            if (phase == KioskTransportPhase.Disconnected && throwOnce)
            {
                throwOnce = false;
                throw new InvalidOperationException("subscriber fault");
            }
        };
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        transport.ForceClose(new Exception("closed"));
        await WaitUntil(() => transport.StartCallCount >= 2 && runtime.IsTransportConnected, 5000);
        Assert.True(transport.StartCallCount >= 2, $"loop died; StartCallCount={transport.StartCallCount}");
    }

    [Fact]
    public async Task runtime_double_dispose_does_not_throw()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient());
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-P6");
        await runtime.StartAsync(default);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
    }

    [Fact]
    public void locked_kiosk_status_not_connected_during_signalr_reconnecting_phase()
    {
        var text = KioskStationStatus.Resolve(
            "PC-A",
            KioskTransportPhase.Reconnecting,
            hubTransportConnected: true,
            signalRAutoReconnecting: true,
            pairingJustFailed: false,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: null,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.DoesNotContain("connected", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reconnecting", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void locked_kiosk_no_session_shows_no_server_response_when_heartbeats_fail()
    {
        var byFailures = KioskStationStatus.Resolve(
            "PC-LOCK",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 2,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.Contains("no server response", byFailures, StringComparison.OrdinalIgnoreCase);
        Assert.False(KioskStationStatus.ContainsConnectedWording(byFailures));

        var byStaleAck = KioskStationStatus.Resolve(
            "PC-LOCK",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow.AddSeconds(-30),
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.Contains("no server response", byStaleAck, StringComparison.OrdinalIgnoreCase);
        Assert.False(KioskStationStatus.ContainsConnectedWording(byStaleAck));
    }

    [Fact]
    public async Task start_async_twice_without_reset_keeps_reconnect_loop()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-M-begin");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await runtime.StartAsync(default);
        transport.ForceClose(new Exception("closed"));
        await WaitUntil(() => transport.StartCallCount >= 2 && runtime.IsTransportConnected, 5000);
        Assert.True(transport.StartCallCount >= 2, $"BeginAsync must restart loop; StartCallCount={transport.StartCallCount}");
    }

    [Fact]
    public async Task two_heartbeat_failures_without_stale_ack_show_no_server_response()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(50),
        };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-M-hbfail");
        var ack = new KioskHeartbeatAck { SessionStatus = "Open", Display = new KioskDisplayDto { EstimatedRemainingSeconds = 600 } };
        transport.HeartbeatHandler = _ => Task.FromResult(ack);
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await Task.Delay(80);
        transport.HeartbeatHandler = _ => throw new InvalidOperationException("hb fail");
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && runtime.ConsecutiveHeartbeatFailures < 2)
        {
            await Task.Delay(20);
        }

        Assert.True(runtime.ConsecutiveHeartbeatFailures >= 2);
        var status = KioskStationStatus.Resolve(
            "PC-M-hbfail",
            KioskTransportPhase.Connected,
            runtime.IsTransportConnected,
            false,
            false,
            true,
            runtime.ConsecutiveHeartbeatFailures,
            DateTime.UtcNow,
            DateTime.UtcNow,
            600L,
            null);
        Assert.Contains("no server response", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task heartbeat_failure_counter_resets_after_successful_ack()
    {
        var failCount = 0;
        var transport = new FakeKioskHubTransport
        {
            HeartbeatHandler = _ =>
            {
                if (Interlocked.Increment(ref failCount) <= 2)
                {
                    throw new InvalidOperationException("hb fail");
                }

                return Task.FromResult(new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Display = new KioskDisplayDto { EstimatedRemainingSeconds = 600 },
                });
            },
        };
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(40),
        };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-Mx");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        await WaitUntil(() => runtime.ConsecutiveHeartbeatFailures >= 2, 3000);
        await WaitUntil(() => runtime.ConsecutiveHeartbeatFailures == 0, 5000);
        Assert.Equal(0, runtime.ConsecutiveHeartbeatFailures);
        var status = KioskStationStatus.Resolve(
            "PC-Mx",
            KioskTransportPhase.Connected,
            runtime.IsTransportConnected,
            false,
            false,
            true,
            runtime.ConsecutiveHeartbeatFailures,
            DateTime.UtcNow,
            DateTime.UtcNow,
            600L,
            null);
        Assert.DoesNotContain("no server response", status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task signalr_reconnected_publishes_connected_phase()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        KioskTransportPhase? last = null;
        runtime.ConnectionStatusChanged += (phase, _) => last = phase;
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-Ms6");
        await runtime.StartAsync(default);
        await runtime.WaitForTransportConnectedAsync(TimeSpan.FromSeconds(5), default);
        transport.SimulateSignalRReconnecting();
        Assert.Equal(KioskTransportPhase.Reconnecting, last);
        transport.SimulateSignalRReconnected();
        Assert.Equal(KioskTransportPhase.Connected, last);
        Assert.Equal(KioskTransportPhase.Connected, runtime.TransportPhase);
    }

    [Fact]
    public void connected_phase_with_signalr_auto_reconnecting_must_not_show_bare_connected()
    {
        var text = KioskStationStatus.Resolve(
            "PC-SR",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: true,
            pairingJustFailed: false,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.False(KioskStationStatus.ContainsConnectedWording(text));
    }

    [Fact]
    public void heartbeat_failures_show_no_server_response_until_next_ack()
    {
        var failed = KioskStationStatus.Resolve(
            "PC-B",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: true,
            consecutiveHeartbeatFailures: 2,
            lastHeartbeatAckUtc: DateTime.UtcNow.AddSeconds(-20),
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: 3600L,
            lastErrorDetail: null);
        Assert.Contains("no server response", failed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("left", failed, StringComparison.OrdinalIgnoreCase);

        var recovered = KioskStationStatus.Resolve(
            "PC-B",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: true,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: 3500L,
            lastErrorDetail: null);
        Assert.Contains("left", recovered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void re_pair_failure_status_not_connected()
    {
        var text = KioskStationStatus.Resolve(
            "PC-C",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: true,
            hasOpenSession: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: null,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.Contains("not connected", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(KioskStationStatus.ContainsConnectedWording(text));
    }

    [Fact]
    public void open_session_connected_without_display_shows_no_server_response_when_heartbeats_fail()
    {
        var text = KioskStationStatus.Resolve(
            "PC-nsr",
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            hasOpenSession: true,
            consecutiveHeartbeatFailures: 3,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail: null);
        Assert.Contains("no server response", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connecting", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void lease_grants_fresh_play_time_when_top_up_extends_same_session()
    {
        var sessionId = Guid.NewGuid();
        var ack = new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Lease = new SignedSessionLease
            {
                Payload = new SessionLeasePayload
                {
                    SessionId = sessionId,
                    StationId = "PC-TU",
                    AuthorizedUntilUtc = DateTime.UtcNow.AddHours(1),
                },
            },
            Display = new KioskDisplayDto { EstimatedRemainingSeconds = 120 },
        };
        Assert.True(KioskStationStatus.LeaseGrantsFreshPlayTime(ack, sessionId, DateTime.UtcNow));

        var stale = new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Lease = new SignedSessionLease
            {
                Payload = new SessionLeasePayload
                {
                    SessionId = sessionId,
                    StationId = "PC-TU",
                    AuthorizedUntilUtc = DateTime.UtcNow.AddMinutes(-5),
                },
            },
            Display = new KioskDisplayDto { EstimatedRemainingSeconds = 0 },
        };
        Assert.False(KioskStationStatus.LeaseGrantsFreshPlayTime(stale, sessionId, DateTime.UtcNow));
    }

    [Fact]
    public void local_lock_status_includes_locked_prefix_and_connection_suffix()
    {
        var text = KioskStationStatus.FormatLocalLockScreen(
            "PC-1",
            leaseExpired: true,
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            lastErrorDetail: null);
        Assert.StartsWith("Locked — lease expired (PC-1) · ", text);
        Assert.Contains("connected", text, StringComparison.OrdinalIgnoreCase);

        var stopped = KioskStationStatus.FormatLocalLockScreen(
            "PC-2",
            leaseExpired: false,
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            consecutiveHeartbeatFailures: 3,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            lastErrorDetail: null);
        Assert.StartsWith("Locked — PC-2 · ", stopped);
        Assert.Contains("no server response", stopped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task transport_signalr_reconnecting_publishes_reconnecting_phase_with_no_session()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, _) => Task.CompletedTask };
        KioskTransportPhase? last = null;
        runtime.ConnectionStatusChanged += (phase, _) => last = phase;
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-Rh");
        await runtime.StartAsync(default);
        transport.SimulateSignalRReconnecting();
        Assert.Equal(KioskTransportPhase.Reconnecting, last);
        var status = KioskStationStatus.Resolve(
            "PC-Rh",
            runtime.TransportPhase,
            runtime.IsTransportConnected,
            transport.SignalRAutoReconnecting,
            false,
            false,
            0,
            null,
            DateTime.UtcNow,
            null,
            null);
        Assert.False(KioskStationStatus.ContainsConnectedWording(status));
    }

    [Fact]
    public async Task dispose_during_failed_start_does_not_throw_non_cancellation_error()
    {
        var transport = new FakeKioskHubTransport
        {
            StartHandler = async (_, ct) =>
            {
                await Task.Delay(100, ct);
                throw new HttpRequestException("down");
            },
        };
        var runtime = new KioskRuntime(() => new HttpClient()) { TestDelayAsync = (_, ct) => Task.Delay(5, ct) };
        runtime.ConfigureForTests(transport, new RecordingQueue(), "http://x", Guid.NewGuid(), "k", "PC-P9");
        await runtime.StartAsync(default);
        await Task.Delay(20);
        await runtime.DisposeAsync();
    }
}