#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Guard.Internet;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Brief §10.4.5–10.4.6 — Guard persistence, drift, and pipe replay on windows-2022.</summary>
[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetGuardPersistenceIntegrationTests
{
    private static void RequireWindowsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Fail("Windows-only Guard internet integration.");
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Assert.Fail("Administrator elevation required for Guard internet integration.");
        }
    }

    private static string[] ResolveServerIps()
    {
        return Dns.GetHostAddresses("api.github.com")
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToArray();
    }

    private static string ExportPublicKeyPem(RSA rsa)
    {
        var pub = DotNetUtilities.GetRsaPublicKey(rsa.ExportParameters(false));
        using var writer = new StringWriter();
        new PemWriter(writer).WriteObject(pub);
        return writer.ToString();
    }

    private static InternetGuardConfig BuildConfig(string publicKeyPem)
    {
        var ips = ResolveServerIps();
        Assert.True(ips.Length > 0, "Could not resolve api.github.com for server allow-list.");
        return new InternetGuardConfig
        {
            StationId = "CI-GUARD",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ServerPublicKeyPem = publicKeyPem,
            ExtraAllowPrograms = Array.Empty<string>(),
        };
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_off_guard_process_restart_reasserts_before_pipe_ready()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new InternetFirewallApplier();
        try
        {
            Assert.Equal("applied", applier.Apply(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));

            WriteInternetConfig(config);
            using var guard = GuardProcessStart.StartGuard();
            await Task.Delay(2_000);

            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "OFF must persist while Guard is running.");
            guard.Kill();
            RunPowerShell(
                "Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' | Select-Object -First 1 | Remove-NetFirewallRule -PolicyStore localhost");
            Assert.True(TryTcp("1.1.1.1", 443, 3_000), "Sanity: rule removal must temporarily open egress.");

            using var guard2 = GuardProcessStart.StartGuard();
            var restored = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < restored)
            {
                if (!TryTcp("1.1.1.1", 443, 500))
                {
                    return;
                }

                await Task.Delay(500);
            }

            Assert.Fail("Guard restart must re-assert OFF rules before serving pipe (egress still blocked within 10s).");
        }
        finally
        {
            applier.RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task internet_off_drift_restores_deleted_rule_within_harness_interval()
    {
        RequireWindowsAdmin();
        var prior = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS", "3000");
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new InternetFirewallApplier();
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", applier.Apply(config, on: false));
            var offPayload = new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, offPayload);

            cts = new CancellationTokenSource();
            var server = new InternetPipeServer(config);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true, Name = "TestInternetPipe" };
            pipeThread.Start();

            RunPowerShell(
                "Get-NetFirewallRule -PolicyStore localhost -Group 'Checkpoint Internet Off' | Select-Object -First 1 | Remove-NetFirewallRule -PolicyStore localhost");
            Assert.True(TryTcp("1.1.1.1", 443, 2_000), "Deleted rule must temporarily allow egress.");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
                if (!TryTcp("1.1.1.1", 443, 500))
                {
                    return;
                }
            }

            Assert.Fail("Drift watchdog must restore OFF rules within 15s when harness interval is 3s.");
        }
        finally
        {
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(5));
            applier.RunResetScript();
            TryDelete(InternetGuardPaths.StatePath);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS", prior);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task internet_off_egress_stays_blocked_when_checkpoint_server_process_killed()
    {
        RequireWindowsAdmin();
        if (!int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out var serverPid)
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_DLL")))
        {
            Assert.Fail("GAMECLUB_SERVER_PID and GAMECLUB_SERVER_DLL must be set on windows-2022 integration.");
        }

        var applier = new InternetFirewallApplier();
        var config = BuildConfig("");
        Process relaunched = null;
        try
        {
            Assert.Equal("applied", applier.Apply(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            var server = Process.GetProcessById(serverPid);
            server.Kill();
            server.WaitForExit(15_000);
            await Task.Delay(2_000);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "L3: OFF rule must stay on the PC when Checkpoint server is unreachable.");

            var dll = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_DLL");
            var psi = new ProcessStartInfo("dotnet", dll)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = entry.Key as string;
                if (!string.IsNullOrEmpty(key) && key.StartsWith("GAMECLUB_", StringComparison.Ordinal))
                {
                    psi.EnvironmentVariables[key] = entry.Value?.ToString() ?? "";
                }
            }

            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")))
            {
                psi.EnvironmentVariables["ASPNETCORE_URLS"] = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL");
            }

            relaunched = Process.Start(psi);
            Assert.NotNull(relaunched);
        }
        finally
        {
            applier.RunResetScript();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_pipe_rejects_older_signed_on_while_still_off()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new InternetFirewallApplier();
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var epoch = Guid.NewGuid();
            var off = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 2,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            Assert.Equal("applied", applier.Apply(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            cts = new CancellationTokenSource();
            var server = new InternetPipeServer(config);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(1_000);

            var staleOn = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 1,
                On = true,
                IssuedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            });
            var client = new GuardInternetClient();
            var response = await client.ApplyAsync(staleOn);
            Assert.Contains("rejected_stale", response, StringComparison.OrdinalIgnoreCase);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "Stale ON must be rejected; firewall stays OFF.");
        }
        finally
        {
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(5));
            applier.RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
        }
    }

    private static SignedInternetCommand Sign(RSA rsa, InternetCommandPayload payload)
    {
        var canonical = LeaseVerifier.CanonicalJson(payload);
        var sig = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return new SignedInternetCommand { Payload = payload, SignatureBase64 = sig };
    }

    private static void WriteInternetConfig(InternetGuardConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InternetGuardPaths.ConfigPath));
        File.WriteAllText(InternetGuardPaths.ConfigPath, JsonSerializer.Serialize(config));
    }

    private static bool TryTcp(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private static string RunPowerShell(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell");
        proc.WaitForExit(60_000);
        return proc.StandardOutput.ReadToEnd();
    }
}
#endif