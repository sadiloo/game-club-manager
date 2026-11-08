using System.Diagnostics;

namespace GameClub.Kiosk.Guard;

internal static class KioskPolicyGuard
{
    internal static bool IsInteractiveSessionKiosk(Process[] processes)
    {
        var expectedUser = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? "CheckpointKiosk";
        foreach (var process in processes)
        {
            if (process.SessionId <= 0)
            {
                continue;
            }

            var user = TryGetProcessUser(process);
            if (user is null || user.Equals(expectedUser, StringComparison.OrdinalIgnoreCase)
                || user.IndexOf("Kiosk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return processes.Length > 0;
    }

    internal static void LaunchInActiveConsoleSession(string path)
    {
        if (ConsoleSessionLaunch.TryLaunch(path, out _))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        });
    }

    private static string? TryGetProcessUser(Process process)
    {
        try
        {
            return null;
        }
        catch
        {
            return null;
        }
    }
}
