using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using GameClub.Kiosk.Guard;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Kills a PowerShell process and its descendants on Windows (net48 has no Kill(entireProcessTree)).</summary>
internal static class InternetPowerShellProcessTree
{
    private const int KillAfterExitWaitMs = 3_000;

    internal static IReadOnlyList<int> LastKillTreeTargets { get; private set; } = Array.Empty<int>();

    internal static void KillTree(int rootPid)
    {
        var captured = CaptureTreePids(rootPid).ToList();
        LastKillTreeTargets = captured;

        foreach (var pid in captured)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch
            {
                /* already gone */
            }
        }

        var survivors = WaitForAllProcessExit(captured, KillAfterExitWaitMs);
        if (survivors.Count > 0)
        {
            GuardWatchdogLog.Append(
                $"PROCESS_TREE label=powershell_kill_survivors pids={string.Join(",", survivors)} waitMs={KillAfterExitWaitMs}");
        }
    }

    internal static bool WaitForProcessExit(int pid, int waitMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (DateTime.UtcNow < deadline)
        {
            if (ProcessHasExited(pid))
            {
                return true;
            }

            ThreadingSleep(50);
        }

        return ProcessHasExited(pid);
    }

    internal static int KillAfterExitWaitMsBudget => KillAfterExitWaitMs;

    private static IEnumerable<int> CaptureTreePids(int rootPid)
    {
        var targets = new HashSet<int> { rootPid };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            for (var pass = 0; pass < 6; pass++)
            {
                var added = false;
                foreach (var (pid, parent) in EnumerateProcessesWindows())
                {
                    if (parent is int p && targets.Contains(p) && targets.Add(pid))
                    {
                        added = true;
                    }
                }

                if (!added)
                {
                    break;
                }
            }
        }

        return targets;
    }

    private static List<int> WaitForAllProcessExit(IReadOnlyList<int> pids, int totalWaitMs)
    {
        if (pids.Count == 0)
        {
            return new List<int>();
        }

        var pending = new HashSet<int>(pids);
        var deadline = DateTime.UtcNow.AddMilliseconds(totalWaitMs);
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            pending.RemoveWhere(ProcessHasExited);
            if (pending.Count == 0)
            {
                break;
            }

            ThreadingSleep(50);
        }

        var survivors = new List<int>();
        foreach (var pid in pids)
        {
            if (!ProcessHasExited(pid))
            {
                survivors.Add(pid);
            }
        }

        return survivors;
    }

    private static bool ProcessHasExited(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void ThreadingSleep(int ms) => System.Threading.Thread.Sleep(ms);

    private static IEnumerable<(int Pid, int? ParentPid)> EnumerateProcessesWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield break;
        }

        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                yield break;
            }

            yield return ((int)entry.th32ProcessID, (int?)entry.th32ParentProcessID);
            while (Process32Next(snapshot, ref entry))
            {
                yield return ((int)entry.th32ProcessID, (int?)entry.th32ParentProcessID);
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}