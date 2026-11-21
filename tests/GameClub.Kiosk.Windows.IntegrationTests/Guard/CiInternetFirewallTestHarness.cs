#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>CI-only runner program allow-list for PC internet firewall integration (not product code).</summary>
internal static class CiInternetFirewallTestHarness
{
    internal static string[] ExtraAllowPrograms()
    {
        var fromEnv = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_EXTRA_ALLOW");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        }

        var paths = new List<string>();
        foreach (var processName in new[] { "Runner.Listener", "Runner.Worker" })
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName(processName);
                foreach (var proc in processes)
                {
                    try
                    {
                        var exe = proc.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                        {
                            paths.Add(exe);
                            var binDir = Path.GetDirectoryName(exe);
                            if (!string.IsNullOrEmpty(binDir))
                            {
                                foreach (var sibling in Directory.EnumerateFiles(binDir, "*.exe"))
                                {
                                    paths.Add(sibling);
                                }
                            }
                        }
                    }
                    catch
                    {
                        /* elevation / access */
                    }
                }
            }
            finally
            {
                if (processes is not null)
                {
                    foreach (var proc in processes)
                    {
                        proc.Dispose();
                    }
                }
            }
        }

        if (paths.Count == 0 && Environment.GetEnvironmentVariable("RUNNER_ROOT") is { } root)
        {
            var listener = Path.Combine(root, "bin", "Runner.Listener.exe");
            var worker = Path.Combine(root, "bin", "Runner.Worker.exe");
            if (File.Exists(listener))
            {
                paths.Add(listener);
            }

            if (File.Exists(worker))
            {
                paths.Add(worker);
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
#endif