#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
        var killed = new List<int>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -NonInteractive -Command "
                    + "\"Get-CimInstance Win32_Process -Filter \\\"Name='powershell.exe'\\\" | "
                    + "Where-Object { $_.CommandLine -like '*Checkpoint Internet Off*' } | "
                    + "Select-Object -ExpandProperty ProcessId\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var lister = Process.Start(psi);
            if (lister is null)
            {
                Console.WriteLine("CI_ORPHAN_INTERNET_PS killed=0 pids=");
                return;
            }

            var output = lister.StandardOutput.ReadToEnd();
            lister.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds / 2));
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(line.Trim(), out var pid) || pid <= 0)
                {
                    continue;
                }

                try
                {
                    using var proc = Process.GetProcessById(pid);
                    proc.Kill();
                    proc.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds / 4));
                    killed.Add(pid);
                }
                catch
                {
                    /* already exited */
                }
            }
        }
        catch
        {
            /* best-effort */
        }

        Console.WriteLine(
            $"CI_ORPHAN_INTERNET_PS killed={killed.Count} pids={string.Join(",", killed.Select(p => p.ToString()))}");
    }
}
#endif