using System;
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
    private Thread? _reconcileWorkThread;

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

    public void ReassertPersisted()
    {
        lock (_firewallGate)
        {
            ReassertPersistedLocked();
        }
    }

    public void Run(CancellationToken ct)
    {
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
                var pipe = server;
                var handler = new Thread(() => HandleConnectedClient(pipe))
                {
                    IsBackground = true,
                    Name = "CheckpointInternetPipeClient",
                };
                handler.Start();
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
    }

    private void HandleConnectedClient(NamedPipeServerStream server)
    {
        var handlerThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            using (server)
            {
                GuardWatchdogLog.Append($"pipe_client_handler_begin thread={handlerThread}");
                var readTimeoutMs = InternetGuardPipePolicy.ResolveReadTimeoutMs();
                GuardWatchdogLog.Append(
                    $"pipe_read_begin thread={handlerThread} budget_ms={readTimeoutMs}");
                var body = ReadRequestLine(server, readTimeoutMs, handlerThread);
                if (body is null)
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
                InternetGuardStartupFirewall.ReconcileOnPipeStart(
                    _config,
                    _firewall,
                    () => !_reconcileAbandoned);
                if (!_reconcileAbandoned)
                {
                    var reassert = Stopwatch.StartNew();
                    lock (_firewallGate)
                    {
                        ReassertPersistedLocked();
                    }

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
        var joined = _reconcileWorkThread.Join(TimeSpan.FromMilliseconds(timeoutMs));
        if (joined)
        {
            _reconcilePhase = 2;
            GuardWatchdogLog.Append($"reconcile_end completed=true elapsed_ms={sw.ElapsedMilliseconds}");
            _reconcileWorkFinished.Set();
        }
        else
        {
            AbandonReconcileAfterTimeout(sw);
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
        _reconcileWorkFinished.Set();
    }

    private void RunDriftWatchdog(CancellationToken ct)
    {
        if (!_reconcileWorkFinished.Wait(TimeSpan.FromMilliseconds(InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs() + 30_000)))
        {
            return;
        }

        GuardWatchdogLog.Append("drift_watchdog_start");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(InternetDriftWatchdog.ResolveIntervalMs());
                if (!_on)
                {
                    lock (_firewallGate)
                    {
                        ReassertPersistedLocked();
                    }
                }
            }
            catch
            {
                /* watchdog stopping */
            }
        }
    }

    private string? ResolveStatusState()
    {
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
                return HandleApplyLocked(doc);
            }
            finally
            {
                Monitor.Exit(_firewallGate);
                GuardWatchdogLog.Append("pipe_gate_exit");
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
        var totalWaitMs = InternetGuardReconcilePolicy.ResolveApplyWaitDuringReconcileMs();
        GuardWatchdogLog.Append($"pipe_gate_wait_begin budget_ms={totalWaitMs}");
        if (!WaitForReconcileApplyWindow(totalWaitMs))
        {
            GuardWatchdogLog.Append("pipe_gate_wait_exit entered=false reason=reconcile_window");
            busyError = "reconcile_busy";
            return false;
        }

        GuardWatchdogLog.Append("pipe_gate_wait_exit entered=true");
        if (Monitor.TryEnter(_firewallGate, totalWaitMs))
        {
            GuardWatchdogLog.Append("pipe_gate_enter ok=true");
            return true;
        }

        GuardWatchdogLog.Append("pipe_gate_enter ok=false");
        busyError = "reconcile_busy";
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

        return JsonSerializer.Serialize(new
        {
            epoch = _epoch,
            version = _appliedVersion,
            on = _on,
            appliedAtUtc = DateTime.UtcNow,
            result = code,
        });
    }
}