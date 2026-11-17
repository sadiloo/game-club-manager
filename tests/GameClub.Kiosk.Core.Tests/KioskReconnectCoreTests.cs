using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Tests.Fakes;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskReconnectCoreTests
{
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
}