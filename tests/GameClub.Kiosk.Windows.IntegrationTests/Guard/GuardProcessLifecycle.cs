#if NET48
#nullable disable
using System;
using System.Diagnostics;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class GuardProcessLifecycle
{
    internal static void StopGuardProcessTree(Process guard, TimeSpan timeout)
    {
        if (guard is null || guard.HasExited)
        {
            return;
        }

        var rootPid = guard.Id;
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {rootPid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds));
        }
        catch
        {
            try
            {
                guard.Kill();
            }
            catch
            {
                /* best-effort */
            }
        }

        guard.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds));
        foreach (var childName in new[] { "GameClub.Kiosk", "GameClub.Kiosk.Guard" })
        {
            foreach (var proc in Process.GetProcessesByName(childName))
            {
                try
                {
                    if (proc.Id == rootPid)
                    {
                        continue;
                    }

                    proc.Kill();
                    proc.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds / 2));
                }
                catch
                {
                    /* best-effort */
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }
}
#endif