using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameClub.Shell.Shell;

internal static class ShellNative
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceStatusEx(IntPtr serviceHandle, int infoLevel, IntPtr buffer, int bufferSize, out int bytesNeeded);

    public static int GetServiceProcessId(string serviceName)
    {
        try
        {
            var services = System.ServiceProcess.ServiceController.GetServices();
            foreach (var svc in services)
            {
                if (!string.Equals(svc.ServiceName, serviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var p in Process.GetProcessesByName("GameClub.Server"))
                {
                    return p.Id;
                }

                foreach (var p in Process.GetProcessesByName("CheckpointServer"))
                {
                    return p.Id;
                }

                return 0;
            }
        }
        catch
        {
        }

        return 0;
    }

    public static DateTime? GetProcessStartTime(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }
}