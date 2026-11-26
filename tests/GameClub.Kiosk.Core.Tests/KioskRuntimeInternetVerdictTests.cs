using System.Text.Json;
using System.Threading;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Tests.Fakes;

namespace GameClub.Kiosk.Core.Tests;

/// <summary>
/// A guard verdict (applied, apply_failed, rejected_*) for an (epoch, version) is final on the kiosk: the version is not
/// resent and its report is not overwritten. Only transport/pipe errors (the guard never decided) are retried.
/// Synchronisation is by gates (TaskCompletionSource / SemaphoreSlim) and runtime hooks, never by sleeping.
/// </summary>
public sealed class KioskRuntimeInternetVerdictTests
{
    private const string StationId = "PC-1";
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(10);

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

    /// <summary>
    /// Mirrors InternetPipeServer.HandleApplyLocked: a transport fault never reaches the guard; otherwise admission
    /// (<see cref="InternetCommandAdmission"/>), then the version is recorded BEFORE the firewall apply runs.
    /// </summary>
    private sealed class AdmissionGuardFake
    {
        private readonly object _gate = new();
        private readonly List<(long Version, string Outcome)> _calls = new();

        public Guid Epoch { get; set; }

        public long AppliedVersion { get; set; }

        public InternetCommandPayload? Applied { get; set; }

        public Func<SignedInternetCommand, Exception?>? TransportFault { get; set; }

        public Func<SignedInternetCommand, CancellationToken, Task<string>> Apply { get; set; } =
            (_, _) => Task.FromResult("""{"on":false,"appliedAtUtc":"2026-01-01T00:00:00.0000000Z"}""");

        public int Count(long version, string outcome)
        {
            lock (_gate)
            {
                return _calls.Count(c => c.Version == version && c.Outcome == outcome);
            }
        }

        public async Task<string> HandleAsync(SignedInternetCommand command, CancellationToken ct)
        {
            var p = command.Payload;
            var fault = TransportFault?.Invoke(command);
            if (fault is not null)
            {
                Record(p.Version, "transport");
                throw fault;
            }

            var admission = InternetCommandAdmission.TryRejectBeforeApply(p, StationId, Epoch, AppliedVersion, Applied);
            if (admission is not null)
            {
                Record(p.Version, admission);
                return JsonSerializer.Serialize(new { error = admission });
            }

            Epoch = p.Epoch;
            AppliedVersion = p.Version;
            Applied = p;
            Record(p.Version, "admitted");
            return await Apply(command, ct);
        }

        private void Record(long version, string outcome)
        {
            lock (_gate)
            {
                _calls.Add((version, outcome));
            }
        }
    }

    private static SignedInternetCommand Command(Guid epoch, long version, bool on = false)
        => new()
        {
            Payload = new InternetCommandPayload
            {
                StationId = StationId,
                Epoch = epoch,
                Version = version,
                On = on,
                IssuedAtUtc = DateTime.UtcNow,
            },
            SignatureBase64 = "dGVzdA==",
        };

    private static KioskRuntime NewRuntime(FakeKioskHubTransport transport, AdmissionGuardFake guard, SemaphoreSlim drains)
    {
        var runtime = new KioskRuntime(() => new HttpClient())
        {
            TestHeartbeatInterval = TimeSpan.FromMilliseconds(1),
            TestGuardInternetApplyAsync = guard.HandleAsync,
        };
        runtime.TestInternetApplyWorkerOnQueueDrainedAsync = () =>
        {
            drains.Release();
            return Task.CompletedTask;
        };
        runtime.ConfigureForTests(transport, new NoOpQueue(), "http://127.0.0.1:5000", Guid.NewGuid(), "key", StationId);
        return runtime;
    }

    private static async Task AwaitAsync(SemaphoreSlim gate, string what)
        => Assert.True(await gate.WaitAsync(GateTimeout), $"timed out waiting for {what}");

    // Pin (1): CI run 1014 race. A slow apply that ends apply_failed, a heartbeat ack listing the same version while
    // the apply is in flight, and another ack listing it exactly as the in-flight key clears. The server must only
    // ever receive apply_failed for that version, never rejected_stale, and the guard must be asked exactly once.
    [Fact]
    public async Task PcInternet_apply_failed_is_final_ack_listing_same_version_mid_apply_never_masks_it_with_rejected_stale()
    {
        var epoch = Guid.NewGuid();
        var v3 = Command(epoch, 3);
        var guard = new AdmissionGuardFake();
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Apply = async (_, ct) =>
        {
            applyEntered.TrySetResult();
            await applyRelease.Task.WaitAsync(ct);
            return """{"error":"apply_failed:harness_injected","on":true,"version":3}""";
        };

        var received = new List<KioskInternetAppliedReport?>();
        var beatsSeen = new SemaphoreSlim(0);
        var beatPermits = new SemaphoreSlim(0);
        var transport = new FakeKioskHubTransport
        {
            // Lockstep "server": record what the kiosk reported, wait for the test, then sign the desired state
            // (always v3, as KioskCheckpointCoordinator does on every ack).
            HeartbeatRequestHandler = async (request, ct) =>
            {
                lock (received)
                {
                    received.Add(request.InternetApplied);
                }

                beatsSeen.Release();
                await beatPermits.WaitAsync(ct);
                return new KioskHeartbeatAck { SessionStatus = "Open", Internet = v3 };
            },
        };

        var drains = new SemaphoreSlim(0);
        var runtime = NewRuntime(transport, guard, drains);
        var firstDrain = new TaskCompletionSource<(int Before, int After)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterDrains = new SemaphoreSlim(0);
        var drainCalls = 0;
        runtime.TestInternetApplyWorkerOnQueueDrainedAsync = () =>
        {
            if (Interlocked.Increment(ref drainCalls) == 1)
            {
                // The ack listing v3 that lands exactly as the in-flight key clears (main run 1014: 14 ms later).
                var before = runtime.InternetApplyActiveWorkerGenerationForTests;
                runtime.TestOfferInternetCommandForTests(v3);
                firstDrain.TrySetResult((before, runtime.InternetApplyActiveWorkerGenerationForTests));
            }
            else
            {
                laterDrains.Release();
            }

            return Task.CompletedTask;
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StartAsync(cts.Token);
        await transport.StartAsync(default);

        await AwaitAsync(beatsSeen, "beat 1");
        beatPermits.Release(); // ack 1 lists v3 -> apply starts
        await applyEntered.Task.WaitAsync(GateTimeout);

        await AwaitAsync(beatsSeen, "beat 2 (mid-apply)");
        beatPermits.Release(); // ack 2 lists v3 while the slow apply is in flight
        await AwaitAsync(beatsSeen, "beat 3 (proves ack 2 was processed)");

        applyRelease.TrySetResult(); // guard answers apply_failed
        var (before, after) = await firstDrain.Task.WaitAsync(GateTimeout);
        if (after != before)
        {
            // A resend was started: let it reach the guard and publish before the server looks again.
            await AwaitAsync(laterDrains, "resend worker drain");
        }

        beatPermits.Release(); // ack 3 lists v3 again
        await AwaitAsync(beatsSeen, "beat 4");
        beatPermits.Release();
        await AwaitAsync(beatsSeen, "beat 5");

        List<KioskInternetAppliedReport> reports;
        lock (received)
        {
            reports = received.Where(r => r is not null).Select(r => r!).ToList();
        }

        Assert.True(reports.Count >= 2, $"server received {reports.Count} reports");
        Assert.DoesNotContain(reports, r => r.Error == "rejected_stale");
        Assert.All(reports, r =>
        {
            Assert.Equal(3, r.Version);
            Assert.Equal("apply_failed:harness_injected", r.Error);
        });
        Assert.Equal(1, guard.Count(3, "admitted"));
        Assert.Equal(0, guard.Count(3, "rejected_stale"));
        Assert.Equal(1, runtime.InternetApplyInvocationCountForTests);

        beatPermits.Release(100);
        await runtime.DisposeAsync();
    }

    // Pin (1b): the guard's verdict is not overwritten by a later result for the same version. A heartbeat restart
    // mid-apply orphans the in-flight v3 (the guard already recorded it), the next ack re-offers v3 to a new worker,
    // which can only get rejected_stale; the published report must stay the guard's apply_failed.
    [Fact]
    public async Task PcInternet_guard_verdict_for_version_is_not_overwritten_by_later_same_version_result()
    {
        var epoch = Guid.NewGuid();
        var v3 = Command(epoch, 3);
        var guard = new AdmissionGuardFake();
        var applyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        guard.Apply = async (_, _) =>
        {
            applyEntered.TrySetResult();
            await applyRelease.Task; // the guard finishes its apply even though the kiosk side was cancelled
            return """{"error":"apply_failed:harness_injected","on":true,"version":3}""";
        };
        var transport = new FakeKioskHubTransport
        {
            HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck { SessionStatus = "Open" }),
        };
        var drains = new SemaphoreSlim(0);
        var runtime = NewRuntime(transport, guard, drains);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StartAsync(cts.Token);

        runtime.TestOfferInternetCommandForTests(v3);
        await applyEntered.Task.WaitAsync(GateTimeout);

        await runtime.StartAsync(cts.Token); // heartbeat restart: in-flight v3 orphaned, queue cleared
        runtime.TestOfferInternetCommandForTests(v3); // next ack lists v3; new worker waits for the orphan
        applyRelease.TrySetResult();
        await AwaitAsync(drains, "new worker drain");

        Assert.Equal(1, guard.Count(3, "admitted"));
        var report = runtime.LastInternetReportForTests;
        Assert.NotNull(report);
        Assert.Equal(3, report!.Version);
        Assert.Equal("apply_failed:harness_injected", report.Error);

        await runtime.DisposeAsync();
    }

    // Pin (2): a transport/pipe error means the guard never decided, so the next offer of the same version is retried
    // and reaches the guard.
    [Theory]
    [InlineData("io", GuardInternetClientErrorCodes.PipeError)]
    [InlineData(GuardInternetClientErrorCodes.ConnectTimeout, GuardInternetClientErrorCodes.ConnectTimeout)]
    [InlineData(GuardInternetClientErrorCodes.IoTimeout, GuardInternetClientErrorCodes.IoTimeout)]
    public async Task PcInternet_transport_error_is_retried_and_reaches_guard(string fault, string expectedCode)
    {
        var epoch = Guid.NewGuid();
        var v1 = Command(epoch, 1);
        var guard = new AdmissionGuardFake();
        var faults = 0;
        guard.TransportFault = _ => Interlocked.Increment(ref faults) == 1
            ? (fault == "io" ? new IOException("pipe broken") : new TimeoutException(fault))
            : null;
        var transport = new FakeKioskHubTransport
        {
            HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck { SessionStatus = "Open" }),
        };
        var drains = new SemaphoreSlim(0);
        var runtime = NewRuntime(transport, guard, drains);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StartAsync(cts.Token);

        runtime.TestOfferInternetCommandForTests(v1);
        await AwaitAsync(drains, "first apply");
        Assert.Equal(expectedCode, runtime.LastInternetReportForTests?.Error);
        Assert.Equal(1, guard.Count(1, "transport"));
        Assert.Equal(0, guard.Count(1, "admitted"));

        var before = runtime.InternetApplyActiveWorkerGenerationForTests;
        runtime.TestOfferInternetCommandForTests(v1); // next ack lists v1 again
        Assert.True(
            runtime.InternetApplyActiveWorkerGenerationForTests > before,
            $"transport error {expectedCode} must be retried on the next offer");
        await AwaitAsync(drains, "retry apply");

        Assert.Equal(1, guard.Count(1, "admitted"));
        var report = runtime.LastInternetReportForTests;
        Assert.NotNull(report);
        Assert.Equal(1, report!.Version);
        Assert.Null(report.Error);
        Assert.Equal(2, runtime.InternetApplyInvocationCountForTests);

        await runtime.DisposeAsync();
    }

    // Pin (3): a genuinely stale version (the guard already applied a newer one in this epoch) is still reported
    // rejected_stale, to the server too; that verdict is final for the version.
    [Fact]
    public async Task PcInternet_genuinely_stale_version_is_reported_rejected_stale()
    {
        var epoch = Guid.NewGuid();
        var v5 = Command(epoch, 5);
        var v3 = Command(epoch, 3);
        var guard = new AdmissionGuardFake { Epoch = epoch, AppliedVersion = 5, Applied = v5.Payload };
        var received = new List<KioskInternetAppliedReport?>();
        var transport = new FakeKioskHubTransport
        {
            HeartbeatRequestHandler = (request, _) =>
            {
                lock (received)
                {
                    received.Add(request.InternetApplied);
                }

                return Task.FromResult(new KioskHeartbeatAck { SessionStatus = "Open" });
            },
        };
        var drains = new SemaphoreSlim(0);
        var runtime = NewRuntime(transport, guard, drains);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StartAsync(cts.Token);
        await transport.StartAsync(default);

        runtime.TestOfferInternetCommandForTests(v3);
        await AwaitAsync(drains, "stale apply");
        var report = runtime.LastInternetReportForTests;
        Assert.NotNull(report);
        Assert.Equal(3, report!.Version);
        Assert.Equal("rejected_stale", report.Error);

        bool ServerSawStale()
        {
            lock (received)
            {
                return received.Any(r => r is { Version: 3, Error: "rejected_stale" });
            }
        }

        var deadline = DateTime.UtcNow + GateTimeout;
        while (!ServerSawStale() && DateTime.UtcNow < deadline)
        {
            await Task.Yield();
        }

        Assert.True(ServerSawStale(), "server never received rejected_stale for v3");

        var before = runtime.InternetApplyActiveWorkerGenerationForTests;
        runtime.TestOfferInternetCommandForTests(v3);
        Assert.Equal(before, runtime.InternetApplyActiveWorkerGenerationForTests);
        Assert.Equal(1, guard.Count(3, "rejected_stale"));
        Assert.Equal(1, runtime.InternetApplyInvocationCountForTests);

        await runtime.DisposeAsync();
    }

    // Pin (4): after apply_failed for v1, a new operator press (v2) still applies normally; v1 is not resent.
    [Fact]
    public async Task PcInternet_new_version_after_apply_failed_applies_normally()
    {
        var epoch = Guid.NewGuid();
        var v1 = Command(epoch, 1);
        var v2 = Command(epoch, 2, on: true);
        var guard = new AdmissionGuardFake
        {
            Apply = (cmd, _) => Task.FromResult(cmd.Payload.Version == 1
                ? """{"error":"apply_failed:harness_injected","on":true,"version":1}"""
                : """{"on":true,"appliedAtUtc":"2026-01-01T00:00:00.0000000Z"}"""),
        };
        var transport = new FakeKioskHubTransport
        {
            HeartbeatHandler = _ => Task.FromResult(new KioskHeartbeatAck { SessionStatus = "Open" }),
        };
        var drains = new SemaphoreSlim(0);
        var runtime = NewRuntime(transport, guard, drains);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runtime.StartAsync(cts.Token);

        runtime.TestOfferInternetCommandForTests(v1);
        await AwaitAsync(drains, "v1 apply");
        Assert.Equal("apply_failed:harness_injected", runtime.LastInternetReportForTests?.Error);

        runtime.TestOfferInternetCommandForTests(v2);
        await AwaitAsync(drains, "v2 apply");
        var report = runtime.LastInternetReportForTests;
        Assert.NotNull(report);
        Assert.Equal(2, report!.Version);
        Assert.Null(report.Error);
        Assert.True(report.On);
        Assert.Equal(1, guard.Count(1, "admitted"));
        Assert.Equal(1, guard.Count(2, "admitted"));
        Assert.Equal(0, guard.Count(2, "rejected_stale"));

        var before = runtime.InternetApplyActiveWorkerGenerationForTests;
        runtime.TestOfferInternetCommandForTests(v2);
        Assert.Equal(before, runtime.InternetApplyActiveWorkerGenerationForTests);
        Assert.Equal(2, runtime.InternetApplyInvocationCountForTests);

        await runtime.DisposeAsync();
    }
}
