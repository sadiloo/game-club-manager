using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

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
            if (string.IsNullOrWhiteSpace(user))
            {
                continue;
            }

            if (string.Equals(user, expectedUser, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static void LaunchInActiveConsoleSession(string path)
    {
        if (ConsoleSessionLaunch.TryLaunch(path, out _))
        {
            return;
        }

        throw new InvalidOperationException("Could not relaunch kiosk in the kiosk user session.");
    }

    private static string? TryGetProcessUser(Process process)
    {
        if (!OpenProcessToken(process.Handle, 8, out var token))
        {
            return null;
        }

        try
        {
            using var user = new WindowsIdentity(token);
            var full = user.Name ?? "";
            if (string.IsNullOrEmpty(full))
            {
                return null;
            }

            var slash = full.LastIndexOf('\\');
            return slash >= 0 ? full.Substring(slash + 1) : full;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
