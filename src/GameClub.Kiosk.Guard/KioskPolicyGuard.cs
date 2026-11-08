using System.Diagnostics;

namespace GameClub.Kiosk.Guard;

internal static class KioskPolicyGuard
{
    internal static bool IsInteractiveSessionKiosk(Process[] processes)
    {
        foreach (var process in processes)
        {
            if (process.SessionId > 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static void LaunchInActiveConsoleSession(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        });
    }
}
