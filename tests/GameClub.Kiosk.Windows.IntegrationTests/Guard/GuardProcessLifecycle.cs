#if NET48
#nullable disable
using System;
using System.Diagnostics;
using GameClub.Kiosk.Windows.IntegrationTests.Server;

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
        var waitMs = (int)Math.Max(1, timeout.TotalMilliseconds);
        L17StepLog.Write("process_tree_kill_begin", $"rootPid={rootPid} wait_ms={waitMs}");
        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {rootPid}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            killer?.WaitForExit(waitMs);
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

        if (!guard.WaitForExit(waitMs))
        {
            try
            {
                using var killer = Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {rootPid}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                killer?.WaitForExit(waitMs);
            }
            catch
            {
                /* best-effort */
            }

            guard.WaitForExit(waitMs);
        }
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

        L17StepLog.Write("process_tree_kill_end", $"rootPid={rootPid} exited={guard.HasExited}");
    }

    internal static void StopOrphanedInternetPowerShell(TimeSpan timeout)
    {
        try
        {
            using var killer = Process.Start(
                new ProcessStartInfo(
                    "powershell.exe",
                    "-NoProfile -NonInteractive -Command \"Get-CimInstance Win32_Process -Filter \\\"Name='powershell.exe'\\\" | "
                    + "Where-Object { $_.CommandLine -match 'Checkpoint|EncodedCommand' } | "
                    + "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            killer?.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds));
        }
        catch
        {
            /* best-effort */
        }
    }
}
#endif