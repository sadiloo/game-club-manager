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
    }

    public static void Unregister(int processId)
    {
        Tracked.TryRemove(processId, out _);
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
}
#endif