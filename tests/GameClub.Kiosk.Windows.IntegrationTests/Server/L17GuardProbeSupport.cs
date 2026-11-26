#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>L17 live probe — preflight logging and bounded pipe readiness wait (harness only).</summary>
internal static class L17GuardProbeSupport
{
    internal static void LogPreflight()
    {
        L17StepLog.Write("preflight_begin");
        var pipeName = GuardInternetClient.PipeName;
        var guardProcs = Process.GetProcessesByName("GameClub.Kiosk.Guard");
        var svcProcs = Process.GetProcessesByName("CheckpointGuard");
        Console.WriteLine(
            $"L17_PROBE_PREFLIGHT pipe_name={pipeName} GameClub.Kiosk.Guard_count={guardProcs.Length} "
            + $"CheckpointGuard_count={svcProcs.Length}");
        foreach (var p in guardProcs)
        {
            Console.WriteLine($"L17_PROBE_PREFLIGHT GameClub.Kiosk.Guard pid={p.Id}");
        }

        foreach (var p in svcProcs)
        {
            Console.WriteLine($"L17_PROBE_PREFLIGHT CheckpointGuard pid={p.Id}");
        }

        L17StepLog.Write("preflight_end", $"pipe_name={pipeName} guard_count={guardProcs.Length}");
    }

    internal static async Task WaitForPipeReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        L17StepLog.Write("pipe_wait_begin", $"timeout_ms={(int)timeout.TotalMilliseconds}");
        var client = new GuardInternetClient();
        var started = DateTime.UtcNow;
        var attempt = 0;
        while (DateTime.UtcNow - started < timeout)
        {
            attempt++;
            L17StepLog.Write("pipe_wait_attempt", $"attempt={attempt}");
            try
            {
                await client.GetStatusAsync(ct).ConfigureAwait(false);
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Console.WriteLine(
                    $"L17_PROBE_PIPE_READY pipe_name={GuardInternetClient.PipeName} attempt={attempt} "
                    + $"elapsed_ms={elapsedMs} timeout_ms={(int)timeout.TotalMilliseconds}");
                Console.Out.Flush();
                L17StepLog.Write("pipe_wait_ready", $"attempt={attempt} elapsed_ms={elapsedMs}");
                return;
            }
            catch (TimeoutException)
            {
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Console.WriteLine(
                    $"L17_PROBE_PIPE_WAIT pipe_name={GuardInternetClient.PipeName} attempt={attempt} "
                    + $"elapsed_ms={elapsedMs} timeout_ms={(int)timeout.TotalMilliseconds}");
                Console.Out.Flush();
                L17StepLog.Write("pipe_wait_timeout_attempt", $"attempt={attempt} elapsed_ms={elapsedMs}");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        L17StepLog.Write("pipe_wait_failed", $"timeout_ms={(int)timeout.TotalMilliseconds}");
        throw new InvalidOperationException(
            $"L17 probe pipe {GuardInternetClient.PipeName} not ready within {(int)timeout.TotalSeconds}s");
    }

    internal static void RestoreFirewallAfterProbeGuard(InternetGuardConfig config)
    {
        L17StepLog.Write("cleanup_firewall_begin", $"stationId={config.StationId}");
        var applier = new InternetFirewallApplier();
        applier.Apply(config, on: true);
        applier.RunResetScript();
        try
        {
            if (File.Exists(InternetGuardPaths.StatePath))
            {
                File.Delete(InternetGuardPaths.StatePath);
            }
        }
        catch
        {
            /* best-effort */
        }

        var ruleCount = ReadCheckpointInternetOffRuleCount();
        Console.WriteLine($"L17_PROBE_CLEANUP checkpoint_internet_off_rule_count={ruleCount}");
        L17StepLog.Write("cleanup_firewall_end", $"rule_count={ruleCount}");
    }

    private static int ReadCheckpointInternetOffRuleCount()
    {
        try
        {
            var script =
                "(Get-NetFirewallRule -Group 'Checkpoint Internet Off' -ErrorAction SilentlyContinue | Measure-Object).Count";
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command " + script)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var text = p!.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10_000);
            return int.TryParse(text, out var count) ? count : -1;
        }
        catch
        {
            return -1;
        }
    }
}
#endif