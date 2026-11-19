#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>L17 live probe — preflight logging and bounded pipe readiness wait (harness only).</summary>
internal static class L17GuardProbeSupport
{
    internal static void LogPreflight()
    {
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
    }

    internal static async Task WaitForPipeReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var client = new GuardInternetClient();
        var started = DateTime.UtcNow;
        var attempt = 0;
        while (DateTime.UtcNow - started < timeout)
        {
            attempt++;
            try
            {
                await client.GetStatusAsync(ct).ConfigureAwait(false);
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Console.WriteLine(
                    $"L17_PROBE_PIPE_READY pipe_name={GuardInternetClient.PipeName} attempt={attempt} "
                    + $"elapsed_ms={elapsedMs} timeout_ms={(int)timeout.TotalMilliseconds}");
                return;
            }
            catch (TimeoutException)
            {
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Console.WriteLine(
                    $"L17_PROBE_PIPE_WAIT pipe_name={GuardInternetClient.PipeName} attempt={attempt} "
                    + $"elapsed_ms={elapsedMs} timeout_ms={(int)timeout.TotalMilliseconds}");
            }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"L17 probe pipe {GuardInternetClient.PipeName} not ready within {(int)timeout.TotalSeconds}s");
    }
}
#endif