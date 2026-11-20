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
    private readonly ManualResetEventSlim _earlyReassertFinished = new(true);
    private volatile bool _earlyReassertApplyBlocked;
    private volatile bool _loggedPipeListening;
    private volatile int _loggedPipeReady;
    private readonly object _heldRequestFifoLock = new();
    private int _heldRequestNextTicket;
    private int _heldRequestServingTicket;
    private Thread? _earlyReassertThread;
    private bool _firstPipeListenerAttemptDone;

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

    private void RunEarlyReassertOffOnBackgroundThread()
    {
        var earlyReassert = Stopwatch.StartNew();
        try
        {
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
        catch (Exception ex)
        {
            _earlyReassertApplyBlocked = true;
            GuardWatchdogLog.Append(
                $"GUARD_TIMELINE event=early_reassert_off_failed error={ex.Message}");
            AppendPipeTimeline($"early_reassert_off_failed error={ex.Message}");
        }
        finally
        {
            InternetGuardShellLaunchGate.MarkReady();
            _earlyReassertFinished.Set();
            PulseHeldRequestFifo();
        }
    }

    private int AcquireHeldRequestTicket()
    {
        lock (_heldRequestFifoLock)
        {
            return _heldRequestNextTicket++;
        }
    }

    private void WaitForHeldRequestTurn(int ticket)
    {
        _earlyReassertFinished.Wait(_shutdownCt);
        lock (_heldRequestFifoLock)
        {
            while (_heldRequestServingTicket != ticket)
            {
                if (_shutdownCt.IsCancellationRequested)
                {
                    return;
                }

                Monitor.Wait(_heldRequestFifoLock, 50);
            }
        }
    }

    private void ReleaseHeldRequestTurn(int ticket)
    {
        lock (_heldRequestFifoLock)
        {
            if (_heldRequestServingTicket == ticket)
            {
                _heldRequestServingTicket++;
            }

            Monitor.PulseAll(_heldRequestFifoLock);
        }
    }

    private void PulseHeldRequestFifo()
    {
        lock (_heldRequestFifoLock)
        {
            Monitor.PulseAll(_heldRequestFifoLock);
        }
    }

    private void TryClearEarlyReassertRefusalAfterVerifiedOffApply()
    {
        if (!_earlyReassertApplyBlocked)
        {
            return;
        }

        _earlyReassertApplyBlocked = false;
        GuardWatchdogLog.Append("GUARD_TIMELINE event=early_reassert_refusal_cleared");
        AppendPipeTimeline("early_reassert_refusal_cleared");
    }

    private static void LogPipeListeningOnce(InternetPipeServer server)
    {
        if (server._loggedPipeListening)
        {
            return;
        }

        server._loggedPipeListening = true;
        GuardWatchdogLog.Append("GUARD_TIMELINE event=pipe_listening");
        server.AppendPipeTimeline("pipe_listening");
    }

    private void StartEarlyReassertOffThread()
    {
        if (_earlyReassertThread is not null || _on)
        {
            return;
        }

        _earlyReassertThread = new Thread(RunEarlyReassertOffOnBackgroundThread)
        {
            IsBackground = true,
            Name = "CheckpointInternetEarlyReassertOff",
        };
        _earlyReassertThread.Start();
    }

    private void MaybeLogPipeReadyAfterRequest(int handlerThread)
    {
        if (Interlocked.CompareExchange(ref _loggedPipeReady, 1, 0) != 0)
        {
            return;
        }

        GuardWatchdogLog.Append(
            $"GUARD_TIMELINE event=pipe_ready thread={handlerThread}");
    }

    public void Run(CancellationToken ct)
    {
        _shutdownCt = ct;
        _runTimelineOriginTicks = Stopwatch.GetTimestamp();
        InternetGuardShellLaunchGate.ResetReady();
        _heldRequestNextTicket = 0;
        _heldRequestServingTicket = 0;
        _earlyReassertApplyBlocked = false;
        _loggedPipeListening = false;
        _loggedPipeReady = 0;
        _earlyReassertThread = null;
        _firstPipeListenerAttemptDone = false;
        AppendPipeTimeline("run_begin");
        if (!_on)
        {
            _earlyReassertFinished.Reset();
        }
        else
        {
            _earlyReassertFinished.Set();
            InternetGuardShellLaunchGate.MarkReady();
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
                var firstListenerAttempt = !_firstPipeListenerAttemptDone;
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        GuardInternetClient.PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    LogPipeListeningOnce(this);
                    if (!_on && _earlyReassertThread is null)
                    {
                        StartEarlyReassertOffThread();
                    }

                    server.WaitForConnection();
                    GuardWatchdogLog.Append(
                        $"pipe_accept thread={Thread.CurrentThread.ManagedThreadId}");
                    var pipe = server;
                    server = null;
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
                finally
                {
                    server?.Dispose();
                    if (firstListenerAttempt)
                    {
                        _firstPipeListenerAttemptDone = true;
                        if (!_on && _earlyReassertThread is null)
                        {
                            StartEarlyReassertOffThread();
                        }
                    }
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
            var earlyReassert = _earlyReassertThread;
            if (earlyReassert is not null)
            {
                JoinPipeBackgroundThread(earlyReassert, "early_reassert_off", 30_000);
            }
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
        var holdTicket = -1;
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
                holdTicket = AcquireHeldRequestTicket();
                WaitForHeldRequestTurn(holdTicket);
                if (_shutdownCt.IsCancellationRequested)
                {
                    return;
                }

                AppendPipeTimeline($"pipe_request_hold_release ticket={holdTicket}");
                string response;
                if (_earlyReassertApplyBlocked && GuardInternetPipeApplyJson.IsOnApplyRequest(body))
                {
                    response = JsonSerializer.Serialize(new { error = "apply_failed:early_reassert_off" });
                }
                else
                {
                    response = HandleRequest(body);
                }

                GuardWatchdogLog.Append(
                    $"GUARD_TIMELINE event=pipe_request_handled thread={handlerThread} ticket={holdTicket}");
                AppendPipeTimeline($"pipe_request_handled ticket={holdTicket}");
                var responseLine = GuardInternetPipeProtocol.FrameRequest(response);
                var outBytes = Encoding.UTF8.GetBytes(responseLine);
                server.Write(outBytes, 0, outBytes.Length);
                server.Flush();
                GuardWatchdogLog.Append(
                    $"pipe_write_ok thread={handlerThread} bytes={outBytes.Length}");
                MaybeLogPipeReadyAfterRequest(handlerThread);
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
        finally
        {
            if (holdTicket >= 0)
            {
                ReleaseHeldRequestTurn(holdTicket);
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

        if (!p.On)
        {
            TryClearEarlyReassertRefusalAfterVerifiedOffApply();
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