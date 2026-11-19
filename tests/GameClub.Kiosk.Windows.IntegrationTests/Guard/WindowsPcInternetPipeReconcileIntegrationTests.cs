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

[Collection(WindowsGuardIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetPipeReconcileIntegrationTests
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

    private static InternetGuardConfig BuildConfig(string publicKeyPem)
    {
        var ips = Dns.GetHostAddresses("api.github.com")
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Distinct()
            .ToArray();
        Assert.True(ips.Length > 0);
        return new InternetGuardConfig
        {
            StationId = "CI-RECONCILE",
            ServerHost = "api.github.com",
            ServerPort = 443,
            ServerIps = ips,
            ServerPublicKeyPem = publicKeyPem,
            ExtraAllowPrograms = Array.Empty<string>(),
        };
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_status_ready_within_2s_while_reconcile_slow()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowReconcileInternetFirewallApplier(resetDelayMs: 60_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", applier.Apply(config, on: false));
            TryDeleteState();
            cts = new CancellationTokenSource();
            var server = new InternetPipeServer(config, applier);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true, Name = "SlowReconcilePipe" };
            var guardStart = DateTime.UtcNow;
            pipeThread.Start();

            string status = null;
            var statusSw = Stopwatch.StartNew();
            while (statusSw.Elapsed < TimeSpan.FromSeconds(2))
            {
                try
                {
                    status = await new GuardInternetClient().GetStatusAsync(default);
                    break;
                }
                catch (TimeoutException)
                {
                    await Task.Delay(50);
                }
            }

            var elapsedMs = (int)(DateTime.UtcNow - guardStart).TotalMilliseconds;
            Assert.NotNull(status);
            Assert.True(elapsedMs < 2_500, $"status took {elapsedMs}ms");
            Assert.Contains("reconciling", status, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"internet_pipe_status_reconciling elapsed_ms={elapsedMs} body={status}");
        }
        finally
        {
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(5));
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_apply_during_reconcile_returns_retryable_reconcile_busy_or_applies_after_wait()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowReconcileInternetFirewallApplier(resetDelayMs: 8_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", applier.Apply(config, on: false));
            TryDeleteState();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, applier).Run(cts.Token))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            await Task.Delay(300);

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var client = new GuardInternetClient();
            var response = await client.ApplyAsync(onCommand);
            if (response.Contains("reconcile_busy", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"internet_pipe_apply_reconcile_busy body={response}");
                return;
            }

            Assert.Contains("applied", response, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"internet_pipe_apply_after_reconcile body={response}");
        }
        finally
        {
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(5));
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_guard_client_connect_timeout_surfaces_as_timeout_exception()
    {
        RequireWindowsAdmin();
        var priorConnect = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", "300");
        try
        {
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await new GuardInternetClient().GetStatusAsync(default));
            Assert.True(sw.ElapsedMilliseconds < 5_000, $"connect wait was {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"internet_guard_client_connect_timeout elapsed_ms={sw.ElapsedMilliseconds}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_CONNECT_MS", priorConnect);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_pipe_status_reports_reconcile_timeout_while_work_thread_hung()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var hung = new HungReconcileInternetFirewallApplier();
        var priorBudget = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", "2000");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", hung.Apply(config, on: false));
            TryDeleteState();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, hung).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);

            var deadline = DateTime.UtcNow.AddSeconds(8);
            string status = null;
            while (DateTime.UtcNow < deadline)
            {
                status = await new GuardInternetClient().GetStatusAsync(default);
                if (status.Contains("reconcile_timeout", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(200);
            }

            Assert.NotNull(status);
            Assert.Contains("reconcile_timeout", status, StringComparison.OrdinalIgnoreCase);

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var applyBody = await new GuardInternetClient().ApplyAsync(onCommand);
            Assert.Contains("applied", applyBody, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"internet_pipe_apply_after_reconcile_timeout body={applyBody}");
        }
        finally
        {
            hung.Release();
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_apply_after_hung_reconcile_timeout_removes_off_rules_when_server_sends_on()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var hung = new HungReconcileInternetFirewallApplier();
        var priorBudget = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", "2000");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var prep = new InternetFirewallApplier();
            Assert.Equal("applied", prep.Apply(config, on: false));
            var offPayload = new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            new InternetGuardStateStore().SaveVerified(offPayload);
            TryDeleteState();

            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, hung).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var status = await new GuardInternetClient().GetStatusAsync(default);
                if (status.Contains("reconcile_timeout", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(200);
            }

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 2,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var applyBody = await new GuardInternetClient().ApplyAsync(onCommand);
            Assert.Contains("applied", applyBody, StringComparison.OrdinalIgnoreCase);

            var ruleCount = RunPowerShell(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim();
            Assert.True(int.TryParse(ruleCount, out var count) && count == 0, $"rule_count={ruleCount}");
        }
        finally
        {
            hung.Release();
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_drift_watchdog_starts_after_reconcile_timeout()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var hung = new HungReconcileInternetFirewallApplier();
        var priorBudget = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        var priorDrift = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", "2000");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS", "500");
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", hung.Apply(config, on: false));
            TryDeleteState();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, hung).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(300);

            var startedAt = File.Exists(logPath) ? new FileInfo(logPath).Length : 0L;
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var sawDrift = false;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(logPath))
                {
                    var text = File.ReadAllText(logPath);
                    if (text.Contains("drift_watchdog_start", StringComparison.Ordinal))
                    {
                        sawDrift = true;
                        break;
                    }
                }

                await Task.Delay(200);
            }

            Assert.True(sawDrift, $"drift_watchdog_start missing in {logPath} after offset={startedAt}");
        }
        finally
        {
            hung.Release();
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS", priorDrift);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task internet_pipe_status_answers_within_2s_while_apply_holds_firewall_gate()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowApplyInternetFirewallApplier(applyOnDelayMs: 30_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, applier).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 2,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });

            var applyTask = Task.Run(async () => await new GuardInternetClient().ApplyAsync(onCommand));
            await Task.Delay(400);
            var statusSw = Stopwatch.StartNew();
            var status = await new GuardInternetClient().GetStatusAsync(default);
            Assert.True(statusSw.ElapsedMilliseconds < 2_500, $"status blocked {statusSw.ElapsedMilliseconds}ms");
            Assert.False(string.IsNullOrWhiteSpace(status));
            Console.WriteLine($"internet_pipe_status_during_slow_apply elapsed_ms={statusSw.ElapsedMilliseconds} body={status}");

            cts.Cancel();
            try
            {
                applyTask.Wait(TimeSpan.FromSeconds(35));
            }
            catch
            {
                /* pipe closed */
            }
        }
        finally
        {
            cts?.Cancel();
            pipeThread?.Join(TimeSpan.FromSeconds(5));
            new InternetFirewallApplier().RunResetScript();
        }
    }

    private static SignedInternetCommand Sign(RSA rsa, InternetCommandPayload payload)
    {
        var canonical = LeaseVerifier.CanonicalJson(payload);
        var sig = Convert.ToBase64String(
            rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return new SignedInternetCommand { Payload = payload, SignatureBase64 = sig };
    }

    private static string ExportPublicKeyPem(RSA rsa)
    {
        var pub = DotNetUtilities.GetRsaPublicKey(rsa.ExportParameters(false));
        using var writer = new StringWriter();
        new PemWriter(writer).WriteObject(pub);
        return writer.ToString();
    }

    private static string RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell");
        proc.WaitForExit(60_000);
        return proc.StandardOutput.ReadToEnd();
    }

    private static void TryDeleteState()
    {
        try
        {
            if (File.Exists(InternetGuardPaths.StatePath))
            {
                File.Delete(InternetGuardPaths.StatePath);
            }
        }
        catch
        {
        }
    }
}
#endif