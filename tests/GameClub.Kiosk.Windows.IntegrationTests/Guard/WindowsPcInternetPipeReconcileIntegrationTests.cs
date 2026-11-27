#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Reflection;
using System.Runtime.CompilerServices;
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

[Collection(PcInternetFirewallWindowsIntegrationCollection.Name)]
[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetPipeReconcileIntegrationTests
{
    private static InternetPipeOwnerDiagnostics.TestScope BeginPipeDiag([CallerMemberName] string test = "")
        => InternetPipeOwnerDiagnostics.BeginTestScope(test);

    private static void RequireWindowsAdmin()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            FirewallTestConsoleDiagnostics.FailAssertion("Windows-only Guard internet integration.");
        }

        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            FirewallTestConsoleDiagnostics.FailAssertion("Administrator elevation required for Guard internet integration.");
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
            ExtraAllowPrograms = CiInternetFirewallTestHarness.ExtraAllowPrograms(),
        };
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_status_ready_within_2s_while_reconcile_slow()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowReconcileInternetFirewallApplier(resetDelayMs: 60_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
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
            StopInternetPipeServer(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_apply_during_reconcile_returns_retryable_reconcile_busy_or_applies_after_wait()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowReconcileInternetFirewallApplier(resetDelayMs: 8_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", applier.ApplyWithStoreEcho(config, on: false));
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
            var applySw = Stopwatch.StartNew();
            var response = await client.ApplyAsync(onCommand);
            applySw.Stop();
            if (response.Contains("reconcile_busy", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"internet_pipe_apply_reconcile_busy elapsed_ms={applySw.ElapsedMilliseconds} body={response}");
                Console.WriteLine(
                    "internet_pipe_apply_rca: server returned reconcile_busy while _reconcilePhase==1 (reconcile_work incl. SlowReconcile RunResetScript delay still running).");
                return;
            }

            Assert.Contains("applied", response, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(
                $"internet_pipe_apply_after_reconcile elapsed_ms={applySw.ElapsedMilliseconds} body={response}");
            Console.WriteLine(
                "internet_pipe_apply_rca: apply handler waited in TryEnterFirewallForApply until _reconcilePhase!=1 and/or acquired _firewallGate after reconcile_work (SlowReconcile reset_delay_ms=8000) finished; see PIPE_TIMELINE pipe_gate_* and reconcile_phase_2 in watchdog.log.");
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_guard_client_connect_timeout_surfaces_as_timeout_exception()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
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

    [Fact(Timeout = 30_000)]
    public async Task internet_guard_client_write_timeout_when_server_accepts_but_never_reads()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        var preflight = InternetPipeOwnerDiagnostics.WritePipeOwner(
            "test_preflight",
            nameof(internet_guard_client_write_timeout_when_server_accepts_but_never_reads));
        Assert.False(
            preflight.PipeReachable,
            $"pipe {GuardInternetClient.PipeName} must be free before stub bind; "
            + $"serverPid={preflight.ServerPid} detail={preflight.Detail}");
        var priorIo = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS", "800");
        NamedPipeServerStream acceptPipe = null;
        Thread acceptThread = null;
        var ready = new ManualResetEventSlim(false);
        var stopStub = new ManualResetEventSlim(false);
        try
        {
            acceptThread = new Thread(() =>
            {
                try
                {
                    acceptPipe = new NamedPipeServerStream(
                        GuardInternetClient.PipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    ready.Set();
                    acceptPipe.WaitForConnection();
                    stopStub.Wait();
                }
                catch (IOException)
                {
                    /* stub teardown */
                }
                catch (ObjectDisposedException)
                {
                    /* stub teardown */
                }
            })
            {
                IsBackground = true,
                Name = "StubNeverReadPipeServer",
            };
            acceptThread.Start();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "stub pipe server did not become ready");

            var sw = Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
                await new GuardInternetClient().GetStatusAsync(default));
            Assert.Equal(GuardInternetClientErrorCodes.IoTimeout, ex.Message);
            Assert.True(sw.ElapsedMilliseconds < 5_000, $"write io wait was {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"internet_guard_client_write_timeout elapsed_ms={sw.ElapsedMilliseconds}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_IO_MS", priorIo);
            stopStub.Set();
            InternetPipeServerTestTeardown.TryUnblock();
            try
            {
                acceptPipe?.Dispose();
            }
            catch
            {
                /* stub teardown */
            }

            if (acceptThread is not null)
            {
                Assert.True(acceptThread.Join(TimeSpan.FromSeconds(5)), "acceptThread did not stop");
            }

            stopStub.Dispose();
            ready.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_pipe_apply_succeeds_after_reconcile_timeout_while_reset_work_thread_hung()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var hung = new HungReconcileInternetFirewallApplier();
        var priorBudget = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", "2000");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", hung.ApplyWithStoreEcho(config, on: false));
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
            StopInternetPipeServer(cts, pipeThread);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task internet_reconcile_abandoned_work_thread_makes_no_further_powershell_calls_after_apply()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var priorBudget = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        var hangRelease = new ManualResetEventSlim(false);
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", "2000");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            Assert.Equal("applied", new InternetFirewallApplier().ApplyWithStoreEcho(config, on: false));
            TryDeleteState();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(
                config,
                firewall: null,
                new InternetPipeServerReconcileTestInjections(hangRelease)).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();

            var reconcileDeadline = DateTime.UtcNow.AddSeconds(5);
            string status = null;
            while (DateTime.UtcNow < reconcileDeadline)
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

            var callsAtAbandon = InternetPowerShellRunner.ReconcileRunCallCountAtAbandon;
            Assert.True(callsAtAbandon >= 0, "abandon boundary should be recorded");

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

            hangRelease.Set();
            var settleDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < settleDeadline)
            {
                var postStatus = await new GuardInternetClient().GetStatusAsync(default);
                if (!postStatus.Contains("reconcile_timeout", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                await Task.Delay(100);
            }

            Assert.Equal(callsAtAbandon, InternetPowerShellRunner.ReconcileRunCallCount);
            Console.WriteLine(
                $"reconcile_abandon_runner_calls_stable at_abandon={callsAtAbandon} final={InternetPowerShellRunner.ReconcileRunCallCount}");
        }
        finally
        {
            hangRelease.Set();
            StopInternetPipeServer(cts, pipeThread);
            hangRelease.Dispose();
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_apply_after_hung_reconcile_timeout_removes_off_rules_when_server_sends_on()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
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
            Assert.Equal("applied", prep.ApplyWithStoreEcho(config, on: false));
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

            var ruleCount = WindowsIntegrationRunPowerShell.Run(
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count").Trim();
            Assert.True(int.TryParse(ruleCount, out var count) && count == 0, $"rule_count={ruleCount}");
        }
        finally
        {
            hung.Release();
            StopInternetPipeServer(cts, pipeThread);
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS", priorBudget);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_drift_watchdog_starts_after_reconcile_timeout()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
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
            Assert.Equal("applied", hung.ApplyWithStoreEcho(config, on: false));
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
                    var text = GuardWatchdogTimelineReader.ReadAllTextWithShare();
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
            StopInternetPipeServer(cts, pipeThread);
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
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        var applier = new SlowApplyInternetFirewallApplier(applyOnDelayMs: 30_000);
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        Task<string> applyTask = null;
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

            applyTask = new GuardInternetClient().ApplyAsync(onCommand);
            await Task.Delay(400);
            var statusSw = Stopwatch.StartNew();
            var status = await new GuardInternetClient().GetStatusAsync(default);
            Assert.True(statusSw.ElapsedMilliseconds < 2_500, $"status blocked {statusSw.ElapsedMilliseconds}ms");
            Assert.False(string.IsNullOrWhiteSpace(status));
            Console.WriteLine($"internet_pipe_status_during_slow_apply elapsed_ms={statusSw.ElapsedMilliseconds} body={status}");
        }
        finally
        {
            applier.ReleaseApplyDelay();
            if (applyTask != null)
            {
                try
                {
                    await applyTask;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"internet_pipe_status_during_slow_apply apply_task_end error={ex.Message}");
                }
            }

            StopInternetPipeServer(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_pipe_guard_writes_nothing_until_full_request_line()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);

            const string lineBody = "{\"verb\":\"status\"}";
            var encoded = Encoding.UTF8.GetBytes(lineBody);
            const int splitAt = 10;
            using var client = new NamedPipeClientStream(
                ".",
                GuardInternetClient.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            client.Connect(3_000);
            client.Write(encoded, 0, splitAt);
            client.Flush();
            AssertNoServerBytesPeekFor(client, TimeSpan.FromSeconds(1));

            var rest = new byte[encoded.Length - splitAt + 1];
            Array.Copy(encoded, splitAt, rest, 0, encoded.Length - splitAt);
            rest[rest.Length - 1] = (byte)'\n';
            client.Write(rest, 0, rest.Length);
            client.Flush();

            var responseBytes = await ReadLineBytesWithinAsync(client, TimeSpan.FromSeconds(2));
            Assert.True(responseBytes.Length >= 1);
            if (responseBytes.Length >= 3)
            {
                Assert.False(responseBytes[0] == 0xEF && responseBytes[1] == 0xBB && responseBytes[2] == 0xBF);
            }

            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(responseBytes));
            Assert.True(doc.RootElement.TryGetProperty("on", out _));
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
        }
    }

    private static void AssertNoServerBytesPeekFor(NamedPipeClientStream client, TimeSpan wait)
    {
        var handle = client.SafePipeHandle;
        Assert.NotNull(handle);
        Assert.False(handle.IsInvalid, "pipe handle unavailable for PeekNamedPipe");

        var deadline = DateTime.UtcNow.Add(wait);
        while (DateTime.UtcNow < deadline)
        {
            if (!PeekNamedPipe(
                    handle.DangerousGetHandle(),
                    null,
                    0,
                    out _,
                    out var totalBytesAvail,
                    out _))
            {
                FirewallTestConsoleDiagnostics.FailAssertion($"PeekNamedPipe failed, win32={Marshal.GetLastWin32Error()}");
            }

            Assert.Equal(0u, totalBytesAvail);
            Thread.Sleep(50);
        }
    }

    private static async Task<byte[]> ReadLineBytesWithinAsync(Stream stream, TimeSpan wait)
    {
        using var buffer = new MemoryStream();
        var deadline = DateTime.UtcNow.Add(wait);
        var one = new byte[1];
        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var readTask = stream.ReadAsync(one, 0, 1);
            if (await Task.WhenAny(readTask, Task.Delay(remaining)) != readTask)
            {
                var partial = Encoding.UTF8.GetString(buffer.ToArray());
                FirewallTestConsoleDiagnostics.FailAssertion(
                    $"timed out waiting for response newline after {wait.TotalSeconds:0}s; bytes so far ({buffer.Length}): {partial}");
            }

            var n = await readTask;
            if (n == 0)
            {
                continue;
            }

            if (one[0] == (byte)'\n')
            {
                Assert.True(buffer.Length > 0, "expected a non-empty response line before newline");
                return buffer.ToArray();
            }

            if (one[0] != (byte)'\r')
            {
                buffer.WriteByte(one[0]);
            }
        }

        var tail = Encoding.UTF8.GetString(buffer.ToArray());
        FirewallTestConsoleDiagnostics.FailAssertion(
            $"timed out waiting for response newline after {wait.TotalSeconds:0}s; bytes so far ({buffer.Length}): {tail}");
        throw new InvalidOperationException("unreachable");
    }

    [Fact(Timeout = 120_000)]
    public async Task internet_pipe_off_apply_failure_restores_on_and_replies_on_true_with_original_error()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        var applier = new OffFailOpenRollbackInternetFirewallApplier();
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, applier).Run(cts.Token))
            {
                IsBackground = true,
                Name = "OffFailOpenPipe",
            };
            pipeThread.Start();
            await WaitForPipeStatusReconcileFinishedAsync(TimeSpan.FromSeconds(60));

            var onRestoreBeforeOff = applier.OnRestoreCount;

            var offCommand = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 2,
                On = false,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var applyBody = await new GuardInternetClient().ApplyAsync(offCommand);
            using var applyDoc = JsonDocument.Parse(applyBody);
            Assert.Equal("apply_failed:verification:test_harness", applyDoc.RootElement.GetProperty("error").GetString());
            Assert.True(applyDoc.RootElement.GetProperty("on").GetBoolean());
            Assert.Equal(1, applier.OffApplyCount);
            Assert.Equal(onRestoreBeforeOff + 1, applier.OnRestoreCount);

            var status = await new GuardInternetClient().GetStatusAsync(default);
            using var statusDoc = JsonDocument.Parse(status);
            Assert.True(statusDoc.RootElement.GetProperty("on").GetBoolean());

            var activeOut = WindowsIntegrationRunPowerShell.Run(
                "@('Domain','Private','Public') | ForEach-Object { (Get-NetFirewallProfile -Name $_ -ErrorAction SilentlyContinue).DefaultOutboundAction.ToString() }");
            foreach (var line in activeOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Assert.NotEqual("Block", line.Trim(), StringComparer.OrdinalIgnoreCase);
            }
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
            TryDeleteState();
        }
    }

    private static async Task WaitForPipeStatusReconcileFinishedAsync(TimeSpan budget)
    {
        var deadline = DateTime.UtcNow.Add(budget);
        while (DateTime.UtcNow < deadline)
        {
            var status = await new GuardInternetClient().GetStatusAsync(default);
            using var doc = JsonDocument.Parse(status);
            if (!doc.RootElement.TryGetProperty("state", out var state)
                || !string.Equals(state.GetString(), "reconciling", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await Task.Delay(50);
        }

        FirewallTestConsoleDiagnostics.FailAssertion($"status still reconciling after {budget.TotalSeconds:0}s");
    }

    private static void StopInternetPipeServer(CancellationTokenSource cts, Thread pipeThread, TimeSpan? joinBudget = null)
        => InternetPipeServerTestTeardown.Stop(cts, pipeThread, joinBudget);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        IntPtr hNamedPipe,
        byte[] lpBuffer,
        uint nBufferSize,
        out uint lpBytesRead,
        out uint lpTotalBytesAvail,
        out uint lpBytesLeftThisMessage);

    [Fact(Timeout = 30_000)]
    public async Task internet_pipe_utf8_persian_field_round_trips_on_real_pipe()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        const string persian = "چیپس و نوشابه";
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        using var rsa = RSA.Create(2048);
        var pub = ExportPublicKeyPem(rsa);
        var config = BuildConfig(pub);
        config.StationId = persian;
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, new FastApplyInternetFirewallApplier()).Run(cts.Token))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            await Task.Delay(500);

            var requestJson = JsonSerializer.Serialize(new { verb = "status", note = persian });
            var expectedBytes = Encoding.UTF8.GetByteCount(requestJson);
            var response = await InvokeGuardClientSendAsync(requestJson);
            using (var doc = JsonDocument.Parse(response))
            {
                Assert.True(doc.RootElement.TryGetProperty("on", out _));
            }

            Assert.True(File.Exists(logPath));
            var utf8Log = GuardWatchdogTimelineReader.ReadAllTextWithShare();
            Assert.Contains($"pipe_read_ok", utf8Log, StringComparison.Ordinal);
            Assert.Contains($"bytes={expectedBytes}", utf8Log, StringComparison.Ordinal);

            var cmd = Sign(rsa, new InternetCommandPayload
            {
                StationId = persian,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var applyBody = await new GuardInternetClient().ApplyAsync(cmd);
            using var applyDoc = JsonDocument.Parse(applyBody);
            Assert.Equal("applied", applyDoc.RootElement.GetProperty("result").GetString());
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_pipe_line_framing_status_within_2s_while_client_pipe_stays_open()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);
            var sw = Stopwatch.StartNew();
            var status = await new GuardInternetClient().GetStatusAsync(default);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2_000, $"status took {sw.ElapsedMilliseconds}ms");
            Assert.False(string.IsNullOrWhiteSpace(status));
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task internet_pipe_line_framing_apply_within_2s_while_client_pipe_stays_open()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        using var rsa = RSA.Create(2048);
        var config = BuildConfig(ExportPublicKeyPem(rsa));
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            TryDeleteState();
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config, new FastApplyInternetFirewallApplier()).Run(cts.Token))
            {
                IsBackground = true,
            };
            pipeThread.Start();
            await WaitForPipeStatusReconcileFinishedAsync(TimeSpan.FromSeconds(60));
            var cmd = Sign(rsa, new InternetCommandPayload
            {
                StationId = config.StationId,
                Epoch = Guid.NewGuid(),
                Version = 1,
                On = true,
                IssuedAtUtc = DateTime.UtcNow,
            });
            var sw = Stopwatch.StartNew();
            var body = await new GuardInternetClient().ApplyAsync(cmd);
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 2_000, $"apply took {sw.ElapsedMilliseconds}ms");
            Assert.False(string.IsNullOrWhiteSpace(body));
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
            new InternetFirewallApplier().RunResetScript();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_pipe_closes_connection_when_client_never_sends_newline_before_read_timeout()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        var priorRead = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS");
        Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS", "500");
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            using var rsa = RSA.Create(2048);
            var config = BuildConfig(ExportPublicKeyPem(rsa));
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);
            using var client = new NamedPipeClientStream(".", GuardInternetClient.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(3_000);
            var partial = Encoding.UTF8.GetBytes("{\"verb\":\"status\"}");
            client.Write(partial, 0, partial.Length);
            client.Flush();
            await Task.Delay(2_000);
            Assert.True(File.Exists(logPath));
            Assert.Contains("pipe_read_timeout", GuardWatchdogTimelineReader.ReadAllTextWithShare(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_PIPE_READ_MS", priorRead);
            StopInternetPipeServer(cts, pipeThread);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task internet_pipe_rejects_oversize_request_line()
    {
        RequireWindowsAdmin();
        using var pipeDiag = BeginPipeDiag();
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "guard",
            "watchdog.log");
        CancellationTokenSource cts = null;
        Thread pipeThread = null;
        try
        {
            using var rsa = RSA.Create(2048);
            var config = BuildConfig(ExportPublicKeyPem(rsa));
            cts = new CancellationTokenSource();
            pipeThread = new Thread(() => new InternetPipeServer(config).Run(cts.Token)) { IsBackground = true };
            pipeThread.Start();
            await Task.Delay(500);
            using var client = new NamedPipeClientStream(".", GuardInternetClient.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(3_000);
            var oversize = new string('a', GuardInternetPipeProtocol.MaxLineBytes + 1);
            var line = Encoding.UTF8.GetBytes(oversize + "\n");
            var wroteIOException = false;
            try
            {
                client.Write(line, 0, line.Length);
                client.Flush();
            }
            catch (IOException)
            {
                wroteIOException = true;
            }

            await Task.Delay(1_000);
            Assert.True(File.Exists(logPath));
            var logText = GuardWatchdogTimelineReader.ReadAllTextWithShare();
            Assert.Contains("pipe_line_oversize", logText, StringComparison.Ordinal);

            var replyRead = 0;
            try
            {
                var replyBuf = new byte[256];
                using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                replyRead = await client.ReadAsync(replyBuf, 0, replyBuf.Length, readCts.Token);
            }
            catch (Exception)
            {
                replyRead = 0;
            }

            if (wroteIOException)
            {
                Assert.True(logText.Contains("pipe_line_oversize", StringComparison.Ordinal) && replyRead == 0);
            }
            else
            {
                Assert.True(replyRead == 0, "expected no successful reply after oversize request");
            }
        }
        finally
        {
            StopInternetPipeServer(cts, pipeThread);
        }
    }

    private static Task<string> InvokeGuardClientSendAsync(string requestJson)
    {
        var method = typeof(GuardInternetClient).GetMethod(
            "SendAsync",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task<string>)method.Invoke(null, new object[] { requestJson, "test", CancellationToken.None });
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