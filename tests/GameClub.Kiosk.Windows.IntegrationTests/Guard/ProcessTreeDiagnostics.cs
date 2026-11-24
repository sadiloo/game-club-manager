#if NET48
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class ProcessTreeDiagnostics
{
    internal static void LogProcessForest(string label)
    {
        var rows = CollectProcessRows();
        foreach (var row in rows)
        {
            Console.WriteLine(
                $"PROCESS_TREE label={label} pid={row.Pid} ppid={row.ParentPid} name={row.Name} startUtc={row.StartUtc}");
        }

        if (rows.Count == 0)
        {
            Console.WriteLine($"PROCESS_TREE label={label} empty=true");
        }
    }

    internal static void LogPidExitState(string label, int pid, int waitMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        var exited = false;
        int? exitCode = null;
        string exitTimeUtc = "n/a";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (proc.HasExited)
                {
                    exited = true;
                    exitCode = proc.ExitCode;
                    try
                    {
                        exitTimeUtc = proc.ExitTime.ToUniversalTime().ToString("o");
                    }
                    catch
                    {
                        /* */
                    }

                    break;
                }
            }
            catch (ArgumentException)
            {
                exited = true;
                break;
            }

            System.Threading.Thread.Sleep(50);
        }

        if (!exited)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                exited = proc.HasExited;
                if (exited)
                {
                    exitCode = proc.ExitCode;
                    try
                    {
                        exitTimeUtc = proc.ExitTime.ToUniversalTime().ToString("o");
                    }
                    catch
                    {
                        /* */
                    }
                }
            }
            catch (ArgumentException)
            {
                exited = true;
            }
        }

        Console.WriteLine(
            $"PROCESS_TREE label={label} pid={pid} exited={exited} exitCode={(exitCode.HasValue ? exitCode.Value.ToString() : "n/a")} "
            + $"exitTimeUtc={exitTimeUtc} waitMs={waitMs}");
    }

    private static List<ProcessRow> CollectProcessRows()
    {
        var rows = new List<ProcessRow>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return rows;
        }

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return rows;
            }

            do
            {
                var pid = (int)entry.th32ProcessID;
                var name = entry.szExeFile ?? "";
                var startUtc = "n/a";
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    startUtc = InternetPipeOwnerDiagnostics.SafeStartTimeUtc(proc);
                }
                catch
                {
                    /* exited */
                }

                rows.Add(new ProcessRow(pid, (int)entry.th32ParentProcessID, name, startUtc));
            }
            while (Process32Next(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return rows;
    }

    private readonly struct ProcessRow
    {
        internal readonly int Pid;
        internal readonly int ParentPid;
        internal readonly string Name;
        internal readonly string StartUtc;

        internal ProcessRow(int pid, int parentPid, string name, string startUtc)
        {
            Pid = pid;
            ParentPid = parentPid;
            Name = name;
            StartUtc = startUtc;
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
#endif