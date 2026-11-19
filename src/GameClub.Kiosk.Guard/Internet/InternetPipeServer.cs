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
    private readonly ManualResetEventSlim _reconcileCompleted = new(false);
    private InternetCommandPayload? _applied;
    private long _appliedVersion;
    private Guid _epoch = Guid.NewGuid();
    private bool _on = true;
    private volatile int _reconcilePhase;

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
                using var server = new NamedPipeServerStream(
                    GuardInternetClient.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8, false, 8192, leaveOpen: true);
                using var writer = new StreamWriter(server, Encoding.UTF8, 4096, leaveOpen: true) { AutoFlush = true };
                var body = reader.ReadToEnd();
                var response = HandleRequest(body);
                writer.Write(response);
            }
            catch
            {
                Thread.Sleep(200);
            }
        }
    }

    private void RunReconcileBackground(CancellationToken ct)
    {
        _reconcilePhase = 1;
        var sw = Stopwatch.StartNew();
        GuardWatchdogLog.Append("reconcile_start");
        var completed = false;
        var timeoutMs = InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs();
        try
        {
            var work = new Thread(() =>
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
            work.Start();
            if (!work.Join(TimeSpan.FromMilliseconds(timeoutMs)))
            {
                GuardWatchdogLog.Append($"reconcile_timeout elapsed_ms={sw.ElapsedMilliseconds} budget_ms={timeoutMs}");
            }
            else
            {
                completed = true;
                GuardWatchdogLog.Append($"reconcile_end completed=true elapsed_ms={sw.ElapsedMilliseconds}");
            }
        }
        catch (Exception ex)
        {
            GuardWatchdogLog.Append($"reconcile_failed:{ex.Message}");
        }
        finally
        {
            _reconcilePhase = 2;
            _reconcileCompleted.Set();
            if (!completed)
            {
                GuardWatchdogLog.Append($"reconcile_end completed=false elapsed_ms={sw.ElapsedMilliseconds}");
            }
        }
    }

    private void RunDriftWatchdog(CancellationToken ct)
    {
        if (!_reconcileCompleted.Wait(TimeSpan.FromMilliseconds(InternetGuardReconcilePolicy.ResolveReconcileTimeoutMs() + 5_000)))
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

    private string HandleRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var verb = doc.RootElement.GetProperty("verb").GetString();
            if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
            {
                if (_reconcilePhase == 1)
                {
                    return JsonSerializer.Serialize(new
                    {
                        state = "reconciling",
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
        if (_reconcilePhase == 1)
        {
            if (!_reconcileCompleted.Wait(TimeSpan.FromMilliseconds(totalWaitMs)))
            {
                busyError = "reconcile_busy";
                return false;
            }
        }

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