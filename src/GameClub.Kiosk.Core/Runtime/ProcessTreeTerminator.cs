using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameClub.Kiosk.Core.Runtime;

internal static class ProcessTreeTerminator
{
    internal static void TerminateTree(int rootPid)
    {
        var targets = new HashSet<int> { rootPid };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            for (var pass = 0; pass < 4; pass++)
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

        foreach (var pid in targets)
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill();
                }
            }
            catch
            {
                // process already gone
            }
        }
    }

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
