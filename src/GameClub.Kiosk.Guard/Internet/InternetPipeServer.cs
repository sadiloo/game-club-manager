using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetPipeServer
{
    private readonly InternetGuardConfig _config;
    private readonly InternetGuardStateStore _state = new();
    private readonly IInternetFirewallApplier _firewall;
    private readonly InternetCommandVerifier _verifier;
    private readonly object _firewallGate = new();
    private readonly ManualResetEventSlim _reconcileWorkFinished = new(false);
    private InternetCommandPayload? _applied;
    private long _appliedVersion;
    private Guid _epoch = Guid.NewGuid();
    private bool _on = true;
    private volatile int _reconcilePhase;
    private volatile bool _reconcileTimedOutWithWorkAlive;
    private volatile bool _reconcileAbandoned;
    private volatile string? _postAbandonReconcileStatus;
    private Thread? _reconcileWorkThread;
    private readonly object _pipeHandlerThreadsLock = new();
    private readonly List<Thread> _pipeHandlerThreads = new();
    private CancellationToken _shutdownCt;
    private long _runTimelineOriginTicks;
    private long _pipeTimelineSeq;

    public InternetPipeServer(InternetGuardConfig config, IInternetFirewallApplier? firewall = null)
    {
        _config = config;
        _firewall = firewall ?? new InternetFirewallApplier();
        _verifier = new InternetCommandVerifier(config.ServerPublicKeyPem);
        var persisted = _state.Load();
        if (persisted is not null)
        {
            _applied = persisted;
            _appliedVersion = persisted.Version;
            _epoch = persisted.Epoch;
            _on = persisted.On;
        }
    }

    private void ReassertPersistedLocked()
    {
        if (_config.ServerIps.Length == 0)
        {
            return;
        }

        _firewall.Apply(_config, _on);
    }

    private void RunWithFirewallGate(string role, Action work)
    {
        var waitSw = Stopwatch.StartNew();
        var threadLabel = Thread.CurrentThread.Name ?? $"tid_{Thread.CurrentThread.ManagedThreadId}";
        AppendPipeTimeline($"pipe_gate_wait role={role} owner={threadLabel}");
        Monitor.Enter(_firewallGate);
        try
        {
            AppendPipeTimeline(
                $"pipe_gate_acquire role={role} owner={threadLabel} wait_ms={waitSw.ElapsedMilliseconds}");
            work();
        }
        finally
        {
            Monitor.Exit(_firewallGate);
            AppendPipeTimeline($"pipe_gate_release role={role} owner={threadLabel}");
        }
    }

    public void ReassertPersisted()
    {
        RunWithFirewallGate("reassert", ReassertPersistedLocked);
    }

    private void AppendPipeTimeline(string eventName)
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - _runTimelineOriginTicks) * 1000 / Stopwatch.Frequency;
        var seq = Interlocked.Increment(ref _pipeTimelineSeq);
        GuardWatchdogLog.Append($"PIPE_TIMELINE seq={seq} ms={elapsedMs} event={eventName}");
    }

    public void Run(CancellationToken ct)
    {
        _shutdownCt = ct;
        _runTimelineOriginTicks = Stopwatch.GetTimestamp();
        InternetGuardShellLaunchGate.ResetReady();
        try
        {
            AppendPipeTimeline("run_begin");
            if (!_on)
            {
                try
                {
                    var earlyReassert = Stopwatch.StartNew();
                    AppendPipeTimeline("early_reassert_off_lock_wait");
                    RunWithFirewallGate("early_reassert", () =>
                    {
                        AppendPipeTimeline("early_reassert_off_lock_enter");
                        ReassertPersistedLocked();
                        AppendPipeTimeline($"early_reassert_off_lock_exit elapsed_ms={earlyReassert.ElapsedMilliseconds}");
                    });

                    GuardWatchdogLog.Append(
                        $"GUARD_TIMELINE event=early_reassert_off elapsed_ms={earlyReassert.ElapsedMilliseconds}");
                }
                finally
                {
                    InternetGuardShellLaunchGate.MarkReady();
                }
            }
            else
            {
                InternetGuardShellLaunchGate.MarkReady();
            }
        }
        catch
        {
            InternetGuardShellLaunchGate.MarkReady();
            throw;
        }

        var reconcileThread = new Thread(() => RunReconcileBackground(ct))
        {
            IsBackground = true,
            Name = "CheckpointInternetReconcile",
        };
        reconcileThread.Start();

        var drift = new Thread(() => RunDriftWatchdog(ct))
        {
            IsBackground = true,
            Name = "CheckpointInternetDrift",
        };
        drift.Start();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var server = new NamedPipeServerStream(
                        GuardInternetClient.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    server.WaitForConnection();
                    GuardWatchdogLog.Append(
                        $"pipe_accept thread={Thread.CurrentThread.ManagedThreadId}");
                    GuardWatchdogLog.Append(
                        $"GUARD_TIMELINE event=pipe_ready thread={Thread.CurrentThread.ManagedThreadId}");
                    var pipe = server;
                    var handler = new Thread(() => HandleConnectedClient(pipe))
                    {
                        IsBackground = true,
                        Name = "CheckpointInternetPipeClient",
                    };
                    lock (_pipeHandlerThreadsLock)
                    {
                        _pipeHandlerThreads.Add(handler);
                    }

                    handler.Start();
                }
                catch
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    Thread.Sleep(200);
                }
            }
        }
        finally
        {
            AppendPipeTimeline("stop_join_begin");
            if (ct.IsCancellationRequested)
            {
                _reconcileAbandoned = true;
                _reconcilePhase = 2;
                _reconcileWorkFinished.Set();
                InternetPowerShellRunner.MarkAbandonBoundary();
                InternetPowerShellRunner.RequestKillInFlight("pipe_shutdown");
            }

            var cancelled = ct.IsCancellationRequested;
            var driftJoinMs = cancelled
                ? InternetPipeServerShutdownBudgets.CancelledBackgroundJoinMs
                : InternetDriftWatchdog.ResolveIntervalMs() + 60_000;
            JoinPipeBackgroundThread(drift, "drift", driftJoinMs);
            var reconcileJoinMs = cancelled
                ? InternetPipeServerShutdownBudgets.CancelledBackgroundJoinMs
                : InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs() + 90_000;
            JoinPipeBackgroundThread(reconcileThread, "reconcile", reconcileJoinMs);
            var work = _reconcileWorkThread;
            if (work is not null)
            {
                var workJoinMs = cancelled
                    ? InternetPipeServerShutdownBudgets.CancelledBackgroundJoinMs
                    : 120_000;
                JoinPipeBackgroundThread(work, "reconcile_work", workJoinMs);
            }

            JoinPipeClientHandlerThreads(InternetPipeServerShutdownBudgets.HandlerJoinBudget);
            AppendPipeTimeline("stop_join_end");
        }
    }

    private void JoinPipeClientHandlerThreads(TimeSpan totalBudget)
    {
        Thread[] snapshot;
        lock (_pipeHandlerThreadsLock)
        {
            snapshot = _pipeHandlerThreads.ToArray();
        }

        var deadline = DateTime.UtcNow.Add(totalBudget);
        foreach (var thread in snapshot)
        {
            if (thread is null || !thread.IsAlive)
            {
                continue;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                GuardWatchdogLog.Append(
                    $"pipe_handler_join_timeout thread={thread.ManagedThreadId}");
                continue;
            }

            if (!thread.Join(remaining))
            {
                GuardWatchdogLog.Append(
                    $"pipe_handler_join_timeout thread={thread.ManagedThreadId}");
            }
        }
    }

    private static void JoinPipeBackgroundThread(Thread thread, string label, int budgetMs)
    {
        if (thread is null || !thread.IsAlive)
        {
            return;
        }

        if (!thread.Join(TimeSpan.FromMilliseconds(budgetMs)))
        {
            GuardWatchdogLog.Append(
                $"pipe_background_join_timeout name={label} budget_ms={budgetMs} thread_id={thread.ManagedThreadId}");
        }
    }

    private void HandleConnectedClient(NamedPipeServerStream server)
    {
        var handlerThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            using (server)
            {
                if (_shutdownCt.IsCancellationRequested)
                {
                    return;
                }

                GuardWatchdogLog.Append($"pipe_client_handler_begin thread={handlerThread}");
                var readTimeoutMs = InternetGuardPipePolicy.ResolveReadTimeoutMs();
                GuardWatchdogLog.Append(
                    $"pipe_read_begin thread={handlerThread} budget_ms={readTimeoutMs}");
                var body = ReadRequestLine(server, readTimeoutMs, handlerThread);
                if (body is null || _shutdownCt.IsCancellationRequested)
                {
                    return;
                }

                GuardWatchdogLog.Append(
                    $"pipe_read_ok thread={handlerThread} bytes={Encoding.UTF8.GetByteCount(body)}");
                var response = HandleRequest(body);
                var responseLine = GuardInternetPipeProtocol.FrameRequest(response);
                var outBytes = Encoding.UTF8.GetBytes(responseLine);
                server.Write(outBytes, 0, outBytes.Length);
                server.Flush();
                GuardWatchdogLog.Append(
                    $"pipe_write_ok thread={handlerThread} bytes={outBytes.Length}");
            }
        }
        catch
        {
            try
            {
                server.Dispose();
            }
            catch
            {
                /* listener continues */
            }
        }
    }

    private static string? ReadRequestLine(NamedPipeServerStream stream, int timeoutMs, int handlerThread)
        => GuardInternetPipeLineReader.ReadLine(
            stream,
            timeoutMs,
            GuardInternetPipeProtocol.MaxLineBytes,
            handlerThread,
            GuardWatchdogLog.Append);

    private void RunReconcileBackground(CancellationToken ct)
    {
        _reconcilePhase = 1;
        var sw = Stopwatch.StartNew();
        InternetPowerShellRunner.ResetReconcileRunTelemetry();
        GuardWatchdogLog.Append("reconcile_start");
        var timeoutMs = InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs();
        _reconcileWorkThread = new Thread(() =>
        {
            InternetPowerShellRunner.SetReconcilePathThread(true);
            try
            {
                var shouldContinue = () => !_reconcileAbandoned && !ct.IsCancellationRequested;
                InternetGuardStartupFirewall.ReconcileOnPipeStart(
                    _config,
                    _firewall,
                    shouldContinue);
                if (!_reconcileAbandoned && !ct.IsCancellationRequested)
                {
                    var reassert = Stopwatch.StartNew();
                    RunWithFirewallGate("reconcile_work", ReassertPersistedLocked);
                    GuardWatchdogLog.Append($"reconcile_step reassert elapsed_ms={reassert.ElapsedMilliseconds}");
                }
                else
                {
                    GuardWatchdogLog.Append("reconcile_step_skipped_abandoned step=reassert_apply");
                }
            }
            catch (Exception ex)
            {
                GuardWatchdogLog.Append($"reconcile_failed error={ex.Message}");
            }
            finally
            {
                InternetPowerShellRunner.SetReconcilePathThread(false);
            }
        })
        {
            IsBackground = true,
            Name = "CheckpointInternetReconcileWork",
        };
        _reconcileWorkThread.Start();
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var joined = false;
        while (!joined && DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested || _reconcileAbandoned)
            {
                break;
            }

            joined = _reconcileWorkThread.Join(TimeSpan.FromMilliseconds(50));
        }

        if (joined)
        {
            _reconcilePhase = 2;
            AppendPipeTimeline($"reconcile_phase_2 completed=true elapsed_ms={sw.ElapsedMilliseconds}");
            GuardWatchdogLog.Append($"reconcile_end completed=true elapsed_ms={sw.ElapsedMilliseconds}");
            _reconcileWorkFinished.Set();
        }
        else if (ct.IsCancellationRequested || _reconcileAbandoned)
        {
            _reconcilePhase = 2;
            AppendPipeTimeline($"reconcile_phase_2 completed=false reason=shutdown elapsed_ms={sw.ElapsedMilliseconds}");
            GuardWatchdogLog.Append(
                $"reconcile_end completed=false reason=shutdown elapsed_ms={sw.ElapsedMilliseconds}");
            _reconcileWorkFinished.Set();
        }
        else
        {
            AbandonReconcileAfterTimeout(sw);
            AppendPipeTimeline($"reconcile_phase_2 completed=false reason=timeout elapsed_ms={sw.ElapsedMilliseconds}");
            var work = _reconcileWorkThread;
            new Thread(() =>
            {
                work?.Join();
                _reconcileTimedOutWithWorkAlive = false;
                GuardWatchdogLog.Append($"reconcile_end completed=false elapsed_ms={sw.ElapsedMilliseconds}");
            })
            {
                IsBackground = true,
                Name = "CheckpointInternetReconcileJoin",
            }.Start();
        }
    }

    private void AbandonReconcileAfterTimeout(Stopwatch sw)
    {
        _reconcileTimedOutWithWorkAlive = true;
        _reconcileAbandoned = true;
        _reconcilePhase = 2;
        var timeoutMs = InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs();
        GuardWatchdogLog.Append(
            $"reconcile_timeout elapsed_ms={sw.ElapsedMilliseconds} budget_ms={timeoutMs}");
        GuardWatchdogLog.Append("reconcile_gate_released abandoned=true");
        InternetPowerShellRunner.MarkAbandonBoundary();
        _postAbandonReconcileStatus = "reconcile_timeout";
        _reconcileWorkFinished.Set();
    }

    private void RunDriftWatchdog(CancellationToken ct)
    {
        try
        {
            var reconcileWaitMs = InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs() + 30_000;
            if (!_reconcileWorkFinished.Wait(TimeSpan.FromMilliseconds(reconcileWaitMs), ct))
            {
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        GuardWatchdogLog.Append("drift_watchdog_start");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (ct.WaitHandle.WaitOne(InternetDriftWatchdog.ResolveIntervalMs()))
                {
                    break;
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (!_on)
                {
                    if (!ct.IsCancellationRequested)
                    {
                        RunWithFirewallGate("drift", ReassertPersistedLocked);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                /* watchdog stopping */
            }
        }
    }

    private string? ResolveStatusState()
    {
        var abandoned = _postAbandonReconcileStatus;
        if (abandoned is not null)
        {
            return abandoned;
        }

        if (ReconcileWorkStillAliveAfterTimeout())
        {
            return "reconcile_timeout";
        }

        if (_reconcilePhase == 1)
        {
            return "reconciling";
        }

        return null;
    }

    private bool ReconcileWorkStillAliveAfterTimeout()
        => _reconcileTimedOutWithWorkAlive && _reconcileWorkThread is { IsAlive: true };

    private string HandleRequest(string body)
    {
        try
        {
            if (_shutdownCt.IsCancellationRequested)
            {
                return JsonSerializer.Serialize(new { error = "shutting_down" });
            }

            using var doc = JsonDocument.Parse(body);
            var verb = doc.RootElement.GetProperty("verb").GetString();
            if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
            {
                var state = ResolveStatusState();
                if (state is not null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        state,
                        epoch = _epoch,
                        version = _appliedVersion,
                        on = _on,
                        appliedAtUtc = _applied?.IssuedAtUtc,
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    epoch = _epoch,
                    version = _appliedVersion,
                    on = _on,
                    appliedAtUtc = _applied?.IssuedAtUtc,
                });
            }

            if (!string.Equals(verb, "apply", StringComparison.OrdinalIgnoreCase))
            {
                return JsonSerializer.Serialize(new { error = "bad_verb" });
            }

            if (!TryEnterFirewallForApply(out var busyError))
            {
                return JsonSerializer.Serialize(new { error = busyError });
            }

            try
            {
                AppendPipeTimeline("apply_begin");
                var response = HandleApplyLocked(doc);
                AppendPipeTimeline("apply_end");
                return response;
            }
            finally
            {
                var threadLabel = Thread.CurrentThread.Name ?? $"tid_{Thread.CurrentThread.ManagedThreadId}";
                Monitor.Exit(_firewallGate);
                GuardWatchdogLog.Append("pipe_gate_exit");
                AppendPipeTimeline($"pipe_gate_release role=handler owner={threadLabel}");
            }
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"apply_failed:{ex.Message}" });
        }
    }

    private bool TryEnterFirewallForApply(out string? busyError)
    {
        busyError = null;
        if (_shutdownCt.IsCancellationRequested)
        {
            busyError = "shutting_down";
            return false;
        }

        var totalWaitMs = InternetGuardReconcilePolicy.ResolveApplyWaitDuringReconcileMs();
        GuardWatchdogLog.Append($"pipe_gate_wait_begin budget_ms={totalWaitMs}");
        AppendPipeTimeline($"pipe_gate_wait_begin budget_ms={totalWaitMs}");
        if (!WaitForReconcileApplyWindow(totalWaitMs))
        {
            GuardWatchdogLog.Append("pipe_gate_wait_exit entered=false reason=reconcile_window");
            AppendPipeTimeline("pipe_gate_wait_exit entered=false reason=reconcile_window");
            busyError = "reconcile_busy";
            return false;
        }

        GuardWatchdogLog.Append("pipe_gate_wait_exit entered=true");
        AppendPipeTimeline("pipe_gate_wait_exit entered=true");
        var handlerLabel = Thread.CurrentThread.Name ?? $"tid_{Thread.CurrentThread.ManagedThreadId}";
        if (TryEnterFirewallGate(totalWaitMs, out var gateWaitMs))
        {
            GuardWatchdogLog.Append("pipe_gate_enter ok=true");
            AppendPipeTimeline(
                $"pipe_gate_acquire role=handler owner={handlerLabel} wait_ms={gateWaitMs} ok=true");
            return true;
        }

        GuardWatchdogLog.Append("pipe_gate_enter ok=false");
        AppendPipeTimeline("pipe_gate_enter ok=false");
        busyError = "reconcile_busy";
        return false;
    }

    private bool TryEnterFirewallGate(int totalWaitMs, out int waitMs)
    {
        var waitSw = Stopwatch.StartNew();
        var deadline = Environment.TickCount + totalWaitMs;
        while (Environment.TickCount < deadline)
        {
            if (_shutdownCt.IsCancellationRequested)
            {
                waitMs = (int)waitSw.ElapsedMilliseconds;
                return false;
            }

            var sliceMs = Math.Min(200, deadline - Environment.TickCount);
            if (sliceMs <= 0)
            {
                break;
            }

            if (Monitor.TryEnter(_firewallGate, sliceMs))
            {
                waitMs = (int)waitSw.ElapsedMilliseconds;
                return true;
            }
        }

        waitMs = (int)waitSw.ElapsedMilliseconds;
        return false;
    }

    private bool WaitForReconcileApplyWindow(int totalWaitMs)
    {
        if (_reconcilePhase != 1 || _reconcileAbandoned)
        {
            return true;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(totalWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_shutdownCt.IsCancellationRequested)
            {
                return false;
            }

            if (_reconcilePhase != 1 || _reconcileAbandoned)
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return _reconcilePhase != 1 || _reconcileAbandoned;
    }

    private string HandleApplyLocked(JsonDocument doc)
    {
        var command = JsonSerializer.Deserialize<SignedInternetCommand>(doc.RootElement.GetProperty("command").GetRawText())!;
        if (!_verifier.TryVerify(command, out var verr))
        {
            return JsonSerializer.Serialize(new { error = verr });
        }

        var p = command.Payload;
        var admissionError = InternetCommandAdmission.TryRejectBeforeApply(
            p,
            _config.StationId,
            _epoch,
            _appliedVersion,
            _applied);
        if (admissionError is not null)
        {
            return JsonSerializer.Serialize(new { error = admissionError });
        }

        _epoch = p.Epoch;
        _appliedVersion = p.Version;
        _on = p.On;
        _applied = p;
        var code = _config.ServerIps.Length == 0 && !p.On
            ? "firewall_not_configured"
            : _firewall.Apply(_config, p.On);
        if (!string.Equals(code, "applied", StringComparison.Ordinal))
        {
            if (!p.On)
            {
                string? restoreLog = null;
                try
                {
                    var restoreCode = _firewall.Apply(_config, on: true);
                    if (!string.Equals(restoreCode, "applied", StringComparison.Ordinal))
                    {
                        restoreLog = restoreCode;
                    }
                }
                catch (Exception ex)
                {
                    restoreLog = ex.Message;
                }

                _on = true;
                if (restoreLog is not null)
                {
                    GuardWatchdogLog.Append($"off_apply_fail_open_restore result={restoreLog}");
                }

                return JsonSerializer.Serialize(new { error = code, on = true, version = _appliedVersion });
            }

            return JsonSerializer.Serialize(new { error = code, on = _on, version = _appliedVersion });
        }

        _state.SaveVerified(p);
        if (!p.On && !File.Exists(InternetGuardPaths.StatePath))
        {
            _firewall.Apply(_config, on: true);
            return JsonSerializer.Serialize(new
            {
                error = "apply_failed:verified_state_missing",
                on = _on,
                version = _appliedVersion,
            });
        }

        var reconcileReason = _postAbandonReconcileStatus;
        if (string.Equals(code, "applied", StringComparison.Ordinal))
        {
            _postAbandonReconcileStatus = null;
        }

        if (reconcileReason is null)
        {
            return JsonSerializer.Serialize(new
            {
                epoch = _epoch,
                version = _appliedVersion,
                on = _on,
                appliedAtUtc = DateTime.UtcNow,
                result = code,
            });
        }

        return JsonSerializer.Serialize(new
        {
            state = reconcileReason,
            epoch = _epoch,
            version = _appliedVersion,
            on = _on,
            appliedAtUtc = DateTime.UtcNow,
            result = code,
        });
    }
}