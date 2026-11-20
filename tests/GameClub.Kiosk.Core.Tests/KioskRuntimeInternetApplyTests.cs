using System.Threading;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Tests.Fakes;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskRuntimeInternetApplyTests
{
    private sealed class NoOpQueue : IOfflineEventQueue
    {
        public Task EnqueueAsync(Guid eventId, string type, string payloadJson, DateTime createdUtc, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<QueuedEvent>> PendingAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<QueuedEvent>>(Array.Empty<QueuedEvent>());

        public Task AckAsync(Guid eventId, CancellationToken ct = default) => Task.CompletedTask;

        public void Close()
        {
        }
    }

    private static SignedInternetCommand Command(Guid epoch, long version, bool on = false)
        => new()
        {
            Payload = new InternetCommandPayload
            {
                StationId = "PC-1",
                Epoch = epoch,
                Version = version,
                On = on,
                IssuedAtUtc = DateTime.UtcNow,
            },
            SignatureBase64 = "dGVzdA==",
        };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Yield();
        }

        Assert.True(condition(), "condition not met before deadline");
    }

    [Fact]
    public async Task PcInternet_heartbeat_continues_during_slow_internet_apply_and_reports_result_on_later_beat()
    {
        var interval = TimeSpan.FromMilliseconds(25);
        var applyBlock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = interval,
        };
        var epoch = Guid.NewGuid();
        var command = Command(epoch, 1);
        var beat = 0;
        transport.HeartbeatHandler = _ =>
        {
            beat++;
            if (beat == 1)
            {
                return Task.FromResult(new KioskHeartbeatAck
                {
                    SessionStatus = "Open",
                    Internet = command,
                });
            }

            return Task.FromResult(new KioskHeartbeatAck { SessionStatus = "Open" });
        };
        runtime.TestGuardInternetApplyAsync = async (_, ct) =>
        {
            applyEntered.TrySetResult();
            await applyBlock.Task.WaitAsync(ct);
            return """{"on":false,"appliedAtUtc":"2026-01-01T00:00:00.0000000Z"}""";
        };
        runtime.ConfigureForTests(transport, new NoOpQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.StartAsync(cts.Token);
        await transport.StartAsync(default);

        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var slowApplyMs = (int)(interval.TotalMilliseconds * 4);
        var hbBeforeRelease = transport.HeartbeatCallCount;
        var deadline = DateTime.UtcNow.AddMilliseconds(slowApplyMs);
        while (DateTime.UtcNow < deadline && !applyBlock.Task.IsCompleted)
        {
            await Task.Yield();
        }

        Assert.True(transport.HeartbeatCallCount >= hbBeforeRelease + 3,
            $"expected >=3 heartbeats during slow apply; had {transport.HeartbeatCallCount}");
        Assert.NotNull(runtime.LastSuccessfulHeartbeatAckUtc);
        Assert.Null(runtime.LastInternetReportForTests);

        applyBlock.TrySetResult();
        await WaitUntilAsync(() => runtime.LastInternetReportForTests is not null, 2_000);
        var reported = runtime.LastInternetReportForTests!;
        Assert.Equal(epoch, reported.Epoch);
        Assert.Equal(1, reported.Version);
        Assert.False(reported.On);
        Assert.Null(reported.Error);

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task PcInternet_internet_apply_fifo_without_reordering_or_duplicate_enqueue()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(20),
        };
        var epoch = Guid.NewGuid();
        var first = Command(epoch, 1, on: true);
        var second = Command(epoch, 2, on: false);
        var gates = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var order = new List<long>();
        var beat = 0;
        transport.HeartbeatHandler = _ =>
        {
            beat++;
            return Task.FromResult(new KioskHeartbeatAck
            {
                SessionStatus = "Open",
                Internet = beat == 1 ? first : beat == 2 ? second : null,
            });
        };
        runtime.TestGuardInternetApplyAsync = async (cmd, ct) =>
        {
            order.Add(cmd.Payload.Version);
            var idx = order.Count - 1;
            await gates[idx].Task.WaitAsync(ct);
            return $$"""{"on":{{cmd.Payload.On.ToString().ToLowerInvariant()}},"appliedAtUtc":"2026-01-01T00:00:00.0000000Z"}""";
        };
        runtime.ConfigureForTests(transport, new NoOpQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.StartAsync(cts.Token);
        await transport.StartAsync(default);

        await WaitUntilAsync(() => order.Count >= 1, 2_000);
        gates[0].TrySetResult();
        await WaitUntilAsync(() => order.Count >= 2, 2_000);
        gates[1].TrySetResult();

        await WaitUntilAsync(
            () => runtime.LastInternetReportForTests is { Version: 2, Error: null },
            2_000);
        Assert.Equal(new long[] { 1, 2 }, order);
        Assert.Equal(2, runtime.InternetApplyInvocationCountForTests);

        await runtime.DisposeAsync();
    }

    [Fact]
    public async Task PcInternet_runtime_stop_during_internet_apply_publishes_report_and_skips_duplicate_on_restart()
    {
        var transport = new FakeKioskHubTransport();
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(20),
        };
        var epoch = Guid.NewGuid();
        var command = Command(epoch, 1);
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Internet = command,
        });
        runtime.TestGuardInternetApplyAsync = async (_, ct) =>
        {
            applyEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            return "{}";
        };
        runtime.ConfigureForTests(transport, new NoOpQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.StartAsync(cts.Token);
        await transport.StartAsync(default);
        await applyEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await runtime.DisposeAsync();
        await WaitUntilAsync(() => runtime.LastInternetReportForTests is not null, 2_000);
        Assert.Equal("pipe_error", runtime.LastInternetReportForTests!.Error);

        var transport2 = new FakeKioskHubTransport();
        var runtime2 = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(20),
        };
        var applyCount = 0;
        runtime2.TestGuardInternetApplyAsync = (_, _) =>
        {
            Interlocked.Increment(ref applyCount);
            return Task.FromResult("""{"on":false}""");
        };
        transport2.HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Internet = command,
        });
        runtime2.ConfigureForTests(transport2, new NoOpQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", "PC-1");
        await runtime2.StartAsync(default);
        await transport2.StartAsync(default);

        await WaitUntilAsync(() => applyCount >= 1, 2_000);
        var duplicateBeats = 0;
        transport2.HeartbeatHandler = _ =>
        {
            duplicateBeats++;
            return Task.FromResult(new KioskHeartbeatAck
            {
                SessionStatus = "Open",
                Internet = command,
            });
        };
        await WaitUntilAsync(() => duplicateBeats >= 3, 2_000);
        Assert.Equal(1, applyCount);

        var v2 = Command(epoch, 2);
        transport2.HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Internet = v2,
        });
        await WaitUntilAsync(() => applyCount >= 2, 2_000);

        await runtime2.DisposeAsync();
    }
}