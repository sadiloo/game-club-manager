#if NET10_0_OR_GREATER
using System.Collections.Concurrent;
using System.Diagnostics;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Tracks live GameClub.Server processes launched by integration tests for teardown checks.</summary>
internal static class KioskLiveServerRegistry
{
    private static readonly ConcurrentDictionary<int, string> Tracked = new();

    public static void RegisterOwned(int processId, string reason)
    {
        Tracked[processId] = reason;
        SyncPidFileForCi();
    }

    public static void Unregister(int processId)
    {
        Tracked.TryRemove(processId, out _);
        SyncPidFileForCi();
    }

    public static IReadOnlyList<int> GetTrackedProcessIds()
        => Tracked.Keys.OrderBy(id => id).ToList();

    /// <summary>Writes every tracked PID (newline-separated) for the parent ci-integration.ps1 process.</summary>
    public static void SyncPidFileForCi()
    {
        var repoRoot = ResolveRepoRoot();
        var logDir = Path.Combine(repoRoot, "artifacts", "kiosk-windows-logs");
        Directory.CreateDirectory(logDir);
        var path = Path.Combine(logDir, "live-server.pid");
        var pids = GetTrackedProcessIds();
        File.WriteAllText(path, pids.Count == 0 ? "" : string.Join(Environment.NewLine, pids));
    }

    /// <summary>Handed-over live server stays on 127.0.0.1:5225 for later CI phases; do not kill at assembly teardown.</summary>
    public static void HandOverToCiIntegration(int processId, string reason)
    {
        Unregister(processId);
        SyncPidFileForCi();
        TraceWrite($"HANDOVER_TO_CI pid={processId} reason={reason}");
    }

    public static void StopAllTrackedOwnedServers()
    {
        foreach (var pid in GetTrackedProcessIds())
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    TraceWrite($"KILL_TRACKED_SERVER pid={pid} reason=owner-test-cleanup");
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(15_000);
                }
            }
            catch (ArgumentException)
            {
                // already exited
            }
            catch
            {
                // best-effort test cleanup
            }
            finally
            {
                Unregister(pid);
            }
        }
    }

    private static void TraceWrite(string message)
    {
        try
        {
            var repoRoot = ResolveRepoRoot();
            var logDir = Path.Combine(repoRoot, "artifacts", "kiosk-windows-logs");
            Directory.CreateDirectory(logDir);
            var line = $"{DateTime.UtcNow:O} {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "live-server-registry.log"), line);
        }
        catch
        {
            // diagnostic only
        }
    }

    public static void AssertNoTrackedServersAlive()
    {
        var alive = new List<string>();
        foreach (var (pid, reason) in Tracked)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    alive.Add($"pid={pid} reason={reason}");
                }
            }
            catch (ArgumentException)
            {
                Tracked.TryRemove(pid, out _);
            }
        }

        if (alive.Count > 0)
        {
            throw new InvalidOperationException(
                "Integration test launched GameClub.Server process(es) still alive at assembly teardown: "
                + string.Join("; ", alive));
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "GameClub.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
#endif