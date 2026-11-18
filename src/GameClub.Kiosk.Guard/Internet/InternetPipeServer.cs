using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetPipeServer
{
    private readonly InternetGuardConfig _config;
    private readonly InternetGuardStateStore _state = new();
    private readonly InternetFirewallApplier _firewall = new();
    private readonly InternetCommandVerifier _verifier;
    private InternetCommandPayload? _applied;
    private long _appliedVersion;
    private Guid _epoch = Guid.NewGuid();
    private bool _on = true;

    public InternetPipeServer(InternetGuardConfig config)
    {
        _config = config;
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

    public void ReassertPersisted()
    {
        if (_config.ServerIps.Length == 0)
        {
            return;
        }

        _firewall.Apply(_config, _on);
    }

    public void Run(CancellationToken ct)
    {
        ReassertPersisted();
        var drift = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Thread.Sleep(60_000);
                    if (!_on)
                    {
                        ReassertPersisted();
                    }
                }
                catch
                {
                    /* watchdog stopping */
                }
            }
        })
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

    private string HandleRequest(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var verb = doc.RootElement.GetProperty("verb").GetString();
            if (string.Equals(verb, "status", StringComparison.OrdinalIgnoreCase))
            {
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
            if (code.StartsWith("apply_failed", StringComparison.Ordinal) || code == "firewall_not_configured")
            {
                return JsonSerializer.Serialize(new { error = code, on = _on, version = _appliedVersion });
            }

            _state.SaveVerified(p);
            return JsonSerializer.Serialize(new
            {
                epoch = _epoch,
                version = _appliedVersion,
                on = _on,
                appliedAtUtc = DateTime.UtcNow,
                result = code,
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"apply_failed:{ex.Message}" });
        }
    }
}