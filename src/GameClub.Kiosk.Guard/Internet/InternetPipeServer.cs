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
        try
        {
            using (server)
            {
                using var reader = new StreamReader(server, Encoding.UTF8, false, 8192, leaveOpen: true);
                using var writer = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
                var body = reader.ReadToEnd();
                var response = HandleRequest(body);
                writer.Write(response);
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

    private void RunReconcileBackground(CancellationToken ct)
    {
        _reconcilePhase = 1;
        var sw = Stopwatch.StartNew();
        GuardWatchdogLog.Append("reconcile_start");
        var timeoutMs = InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs();
        _reconcileWorkThread = new Thread(() =>
        {
            lock (_firewallGate)
            {
                InternetGuardStartupFirewall.ReconcileOnPipeStart(_config, _firewall);
                ReassertPersistedLocked();
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
            _reconcileTimedOutWithWorkAlive = true;
            GuardWatchdogLog.Append($"reconcile_timeout elapsed_ms={sw.ElapsedMilliseconds} budget_ms={timeoutMs}");
            var work = _reconcileWorkThread;
            new Thread(() =>
            {
                work.Join();
                _reconcileTimedOutWithWorkAlive = false;
                _reconcilePhase = 2;
                GuardWatchdogLog.Append($"reconcile_end completed=false elapsed_ms={sw.ElapsedMilliseconds}");
                _reconcileWorkFinished.Set();
            })
            {
                IsBackground = true,
                Name = "CheckpointInternetReconcileJoin",
            }.Start();
        }
    }

    private void RunDriftWatchdog(CancellationToken ct)
    {
        if (!_reconcileWorkFinished.Wait(TimeSpan.FromMilliseconds(InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs() + 120_000)))
        {
            return;
        }

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
        if (Monitor.TryEnter(_firewallGate, totalWaitMs))
        {
            return true;
        }

        busyError = "reconcile_busy";
        return false;
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
            return JsonSerializer.Serialize(new { error = code, on = _on, version = _appliedVersion });
        }

        _state.SaveVerified(p);
        if (!p.On)
        {
            var readback = InternetFirewallOffApplyReadback.Capture(_config);
            var postStateFailure = readback.ClassifyOff(requireVerifiedStateFile: true);
            if (postStateFailure is not null)
            {
                _firewall.Apply(_config, on: true);
                return JsonSerializer.Serialize(new { error = postStateFailure, on = _on, version = _appliedVersion });
            }
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