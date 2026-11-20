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

    private const string TamperOpenEgressScript =
        "Set-NetFirewallProfile -Name Domain,Private,Public -DefaultOutboundAction NotConfigured; "
        + "Get-NetFirewallRule -Group 'Checkpoint Internet Off' | Select-Object -First 1 | Remove-NetFirewallRule -ErrorAction SilentlyContinue";

    [Fact(Timeout = 60_000)]
    public async Task internet_corrupt_guard_config_still_launches_shell_within_shell_gate()
    {
        RequireWindowsAdmin();
        var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
        Directory.CreateDirectory(Path.GetDirectoryName(InternetGuardPaths.ConfigPath)!);
        string prior = null;
        if (File.Exists(InternetGuardPaths.ConfigPath))
        {
            prior = File.ReadAllText(InternetGuardPaths.ConfigPath);
        }

        File.WriteAllText(InternetGuardPaths.ConfigPath, "{ not-valid-json");
        Process guard = null;
        try
        {
            guard = GuardProcessStart.StartGuard();
            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline)
            {
                var launched = GuardWatchdogTimelineReader.ReadLinesSince(logOffset)
                    .Any(l => l.Contains("guard.restart", StringComparison.Ordinal)
                        && l.Contains("launched_pid_", StringComparison.Ordinal));
                if (launched)
                {
                    return;
                }

                await Task.Delay(500);
            }

            Assert.Fail("Corrupt internet guard config must not block kiosk shell launch (shell gate / MarkReady).");
        }
        finally
        {
            if (guard is not null)
            {
                GuardProcessLifecycle.StopGuardProcessTree(guard, TimeSpan.FromSeconds(8));
                guard.Dispose();
            }

            if (prior is not null)
            {
                File.WriteAllText(InternetGuardPaths.ConfigPath, prior);
            }
            else
            {
                TryDelete(InternetGuardPaths.ConfigPath);
            }
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_off_guard_process_restart_reasserts_before_pipe_ready()
    {
        RequireWindowsAdmin();
        var timeline = Stopwatch.StartNew();
        void GuardTimeline(string evt) =>
            Console.WriteLine($"GUARD_TIMELINE ms={timeline.ElapsedMilliseconds} event={evt}");

        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new InternetFirewallApplier();
        Process guard = null;
        Process guard2 = null;
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            GuardTimeline("initial_off_applied");

            var offPayload = new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            };
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, offPayload);

            WriteInternetConfig(config);
            guard = GuardProcessStart.StartGuard();
            GuardTimeline($"guard1_start pid={guard.Id}");
            await Task.Delay(2_000);

            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "OFF must persist while Guard is running.");
            GuardTimeline("guard1_stop_begin");
            GuardProcessLifecycle.StopGuardProcessTree(guard, TimeSpan.FromSeconds(8));
            guard.Dispose();
            guard = null;
            GuardTimeline("guard1_stop_end");
            GuardProcessLifecycle.StopOrphanedInternetPowerShell(TimeSpan.FromSeconds(5));
            await Task.Delay(500);

            WindowsIntegrationRunPowerShell.Run(TamperOpenEgressScript);
            Assert.True(TryTcp("1.1.1.1", 443, 3_000), "Sanity: profile tamper must temporarily open egress.");
            GuardTimeline("tamper_egress_open");

            var guard2LogOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            guard2 = GuardProcessStart.StartGuard();
            GuardTimeline($"guard2_start pid={guard2.Id}");
            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                guard2LogOffset,
                "GUARD_TIMELINE event=pipe_listening",
                TimeSpan.FromSeconds(20));
            GuardTimeline("pipe_listening_seen");

            var pipeClient = new GuardInternetClient();
            await pipeClient.GetStatusAsync();
            GuardTimeline("guard2_status_ok");

            var restored = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < restored)
            {
                if (!TryTcp("1.1.1.1", 443, 500))
                {
                    GuardTimeline($"first_egress_blocked ms={timeline.ElapsedMilliseconds}");
                    GuardWatchdogTimelineReader.AssertExactlyOneGuardRestart(guard2LogOffset);
                    GuardWatchdogTimelineReader.AssertPipeRestartHandlingOrder(
                        guard2LogOffset,
                        line => GuardTimeline($"watchdog {line}"));
                    GuardWatchdogTimelineReader.AssertEarlyOffReassertBeforePipeReady(
                        guard2LogOffset,
                        line => GuardTimeline($"watchdog {line}"));
                    return;
                }

                await Task.Delay(500);
            }

            GuardTimeline("timeout egress_still_open");
            foreach (var line in GuardWatchdogTimelineReader.ReadLinesSince(guard2LogOffset))
            {
                if (line.Contains("GUARD_TIMELINE", StringComparison.Ordinal)
                    || line.Contains("PIPE_TIMELINE", StringComparison.Ordinal))
                {
                    GuardTimeline($"watchdog {line}");
                }
            }

            Assert.Fail("Guard restart must re-assert OFF rules before serving pipe (egress still blocked within 10s).");
        }
        finally
        {
            if (guard2 is not null)
            {
                try
                {
                    if (!guard2.HasExited)
                    {
                        guard2.Kill();
                    }
                }
                catch
                {
                    /* teardown */
                }

                guard2.Dispose();
            }

            if (guard is not null)
            {
                try
                {
                    if (!guard.HasExited)
                    {
                        guard.Kill();
                    }
                }
                catch
                {
                    /* teardown */
                }

                guard.Dispose();
            }

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
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
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

            WindowsIntegrationRunPowerShell.Run(TamperOpenEgressScript);
            Assert.True(TryTcp("1.1.1.1", 443, 2_000), "Profile tamper must temporarily allow egress.");

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
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
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
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            Assert.False(TryTcp("1.1.1.1", 443, 2_000));
            var server = Process.GetProcessById(serverPid);
            server.Kill();
            server.WaitForExit(15_000);
            await Task.Delay(2_000);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "L3: OFF rule must stay on the PC when Checkpoint server is unreachable.");

            var dll = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_DLL");
            var hostArgs = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_HOST_ARGS");
            if (string.IsNullOrWhiteSpace(hostArgs))
            {
                hostArgs = "--pc-band day";
            }

            Console.WriteLine($"live_server_guard_restart {hostArgs}");
            var execArgs = $"exec \"{dll}\" {hostArgs}";
            var psi = new ProcessStartInfo("dotnet", execArgs)
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

    [Fact(Timeout = 90_000)]
    public async Task internet_pipe_status_answers_within_2s_while_early_reassert_holds()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new DelayedEarlyReassertInternetFirewallApplier(offApplyDelayMs: 5_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var epoch = Guid.NewGuid();
            var off = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, applier).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "early_reassert_off_lock_enter",
                TimeSpan.FromSeconds(20));

            var sw = Stopwatch.StartNew();
            var status = await new GuardInternetClient().GetStatusAsync(default);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2_500, $"status blocked {sw.ElapsedMilliseconds}ms during early reassert");
            using var doc = JsonDocument.Parse(status);
            Assert.True(doc.RootElement.TryGetProperty("pending", out var pending) && pending.GetBoolean());
            Assert.Equal("early_reassert", doc.RootElement.GetProperty("phase").GetString());

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 2,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var applyTask = new GuardInternetClient().ApplyAsync(onCommand);
            var applyResponse = await applyTask;
            Assert.Contains("applied", applyResponse, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_on_apply_during_early_off_reassert_waits_for_lock_exit()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new DelayedEarlyReassertInternetFirewallApplier(offApplyDelayMs: 4_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var epoch = Guid.NewGuid();
            var off = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            cts = new CancellationTokenSource();
            var server = new InternetPipeServer(config, applier);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "early_reassert_off_lock_enter",
                TimeSpan.FromSeconds(20));

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 2,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var client = new GuardInternetClient();
            var applyTask = client.ApplyAsync(onCommand);

            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "early_reassert_off_lock_exit",
                TimeSpan.FromSeconds(25));
            var response = await applyTask;
            Assert.Contains("\"on\":true", response, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("applied", response, StringComparison.OrdinalIgnoreCase);

            var lockExitIdx = GuardWatchdogTimelineReader.ReadLinesSince(logOffset)
                .Select((line, idx) => (line, idx))
                .First(p => p.line.Contains("early_reassert_off_lock_exit", StringComparison.Ordinal)).idx;
            var handledIdx = GuardWatchdogTimelineReader.ReadLinesSince(logOffset)
                .Select((line, idx) => (line, idx))
                .First(p => p.line.Contains("GUARD_TIMELINE event=pipe_request_handled", StringComparison.Ordinal)).idx;
            Assert.True(lockExitIdx < handledIdx, "ON apply must not be handled before early_reassert_off_lock_exit");
            Assert.True(TryTcp("1.1.1.1", 443, 2_000), "ON apply during early reassert must open egress after hold release.");
        }
        finally
        {
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task internet_pipe_on_apply_rejected_when_early_reassert_fails()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var setup = new InternetFirewallApplier();
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var epoch = Guid.NewGuid();
            var off = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            Assert.Equal("applied", setup.ApplyWithStoreEcho(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            cts = new CancellationTokenSource();
            var throwing = new ThrowingEarlyReassertInternetFirewallApplier();
            var server = new InternetPipeServer(config, throwing);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();

            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "GUARD_TIMELINE event=early_reassert_off_failed",
                TimeSpan.FromSeconds(25));

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 2,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var client = new GuardInternetClient();
            var response = await client.ApplyAsync(onCommand);
            Assert.Contains("apply_failed:early_reassert_off", response, StringComparison.OrdinalIgnoreCase);
            Assert.False(TryTcp("1.1.1.1", 443, 2_000), "ON apply must not open egress when early reassert failed.");
        }
        finally
        {
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
            setup.RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_on_apply_allowed_after_off_apply_when_early_reassert_failed()
    {
        RequireWindowsAdmin();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var setup = new InternetFirewallApplier();
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            var epoch = Guid.NewGuid();
            var off = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 1,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            Assert.Equal("applied", setup.ApplyWithStoreEcho(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            cts = new CancellationTokenSource();
            var throwing = new ThrowingEarlyReassertInternetFirewallApplier();
            var server = new InternetPipeServer(config, throwing);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();

            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "GUARD_TIMELINE event=early_reassert_off_failed",
                TimeSpan.FromSeconds(25));

            var offAgain = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 2,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var client = new GuardInternetClient();
            var offResponse = await client.ApplyAsync(offAgain);
            Assert.Contains("applied", offResponse, StringComparison.OrdinalIgnoreCase);
            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "GUARD_TIMELINE event=early_reassert_refusal_cleared",
                TimeSpan.FromSeconds(30));

            var onCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = epoch,
                Version = 3,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var onResponse = await client.ApplyAsync(onCommand);
            Assert.Contains("applied", onResponse, StringComparison.OrdinalIgnoreCase);
            Assert.True(TryTcp("1.1.1.1", 443, 2_000), "ON apply must open egress after refusal cleared.");
        }
        finally
        {
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
            setup.RunResetScript();
            TryDelete(InternetGuardPaths.ConfigPath);
            TryDelete(InternetGuardPaths.StatePath);
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
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
            InternetVerifiedStateFile.Save(InternetGuardPaths.StatePath, off.Payload);
            WriteInternetConfig(config);

            cts = new CancellationTokenSource();
            var logOffset = GuardWatchdogTimelineReader.CurrentLogLength();
            var server = new InternetPipeServer(config);
            pipeThread = new Thread(() => server.Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await GuardWatchdogTimelineReader.WaitForTimelineMarkerAsync(
                logOffset,
                "GUARD_TIMELINE event=pipe_listening",
                TimeSpan.FromSeconds(20));

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
            InternetPipeServerTestTeardown.Stop(cts, pipeThread);
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

}
#endif