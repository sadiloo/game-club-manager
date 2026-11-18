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

    public static void StopAllTrackedOwnedServers()
    {
        foreach (var pid in GetTrackedProcessIds())
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
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
                // best-effort assembly teardown
            }
            finally
            {
                Unregister(pid);
            }
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