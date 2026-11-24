# Windows-only: visible window + child control text for setup PIDs (UTF-8).
if (-not ($IsWindows -or $env:OS -match 'Windows')) {
    function Get-CheckpointSetupUiText { param([int[]] $ProcessIds) return @() }
    return
}

if (-not ("UiTextCapture" -as [type])) {
    Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UiTextCapture {
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private class Collector {
        public HashSet<uint> TargetPids = new HashSet<uint>();
        public List<string> Lines = new List<string>();
    }

    private static void AddLine(Collector c, IntPtr hWnd, uint pid, string kind) {
        if (!IsWindowVisible(hWnd)) return;
        var title = new StringBuilder(8192);
        var cls = new StringBuilder(256);
        GetWindowText(hWnd, title, title.Capacity);
        GetClassName(hWnd, cls, cls.Capacity);
        var t = title.ToString().Trim();
        if (t.Length == 0 && cls.ToString() == "Static") return;
        c.Lines.Add(string.Format("pid={0} kind={1} class={2} text={3}", pid, kind, cls, t));
    }

  private static bool EnumChild(IntPtr hWnd, IntPtr lParam) {
        var c = GCHandle.FromIntPtr(lParam).Target as Collector;
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (!c.TargetPids.Contains(pid)) return true;
        AddLine(c, hWnd, pid, "child");
        return true;
    }

    private static bool EnumTop(IntPtr hWnd, IntPtr lParam) {
        var c = GCHandle.FromIntPtr(lParam).Target as Collector;
        uint pid;
        GetWindowThreadProcessId(hWnd, out pid);
        if (!c.TargetPids.Contains(pid)) return true;
        AddLine(c, hWnd, pid, "window");
        EnumChildWindows(hWnd, EnumChild, lParam);
        return true;
    }

    public static string[] Capture(uint[] pids) {
        var c = new Collector();
        foreach (var p in pids) c.TargetPids.Add(p);
        var g = GCHandle.Alloc(c);
        try {
            EnumWindows(EnumTop, GCHandle.ToIntPtr(g));
        } finally {
            g.Free();
        }
        return c.Lines.ToArray();
    }
}
"@
}

function Get-CheckpointSetupUiText {
    param(
        [int[]] $ProcessIds = @(),
        [string] $ProcessNamePattern = "CheckpointServerSetup*"
    )
    if ($ProcessIds.Count -eq 0) {
        $ProcessIds = @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -like $ProcessNamePattern } |
            ForEach-Object { $_.Id })
    }
    if ($ProcessIds.Count -eq 0) { return @() }
    $uints = @($ProcessIds | ForEach-Object { [uint32]$_ })
    return [string[]][UiTextCapture]::Capture($uints)
}