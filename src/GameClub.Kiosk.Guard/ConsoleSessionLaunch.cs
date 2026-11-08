using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameClub.Kiosk.Guard;

internal static class ConsoleSessionLaunch
{
    private const int WtsCurrentServerHandle = 0;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr pWinStationName;
        public WtsConnectState State;
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init,
    }

    internal static bool TryLaunch(string executablePath, out string detail)
    {
        var kioskUser = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_USER") ?? "CheckpointKiosk";
        var sessionId = FindActiveUserSession(kioskUser);
        if (sessionId is null)
        {
            detail = "kiosk_user_session_not_found";
            return false;
        }

        if (!WTSQueryUserToken(sessionId.Value, out var token) || token == IntPtr.Zero)
        {
            detail = $"no_token_session_{sessionId}";
            return false;
        }

        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            var cmd = $"\"{executablePath}\"";
            var ok = CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero,
                Path.GetDirectoryName(executablePath), ref si, out var pi);
            if (!ok)
            {
                detail = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (pi.hProcess != IntPtr.Zero)
            {
                Process.GetProcessById(pi.dwProcessId);
            }

            detail = $"launched_pid_{pi.dwProcessId}";
            return true;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("Wtsapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    private enum WtsInfoClass
    {
        WTSUserName = 5,
    }

    internal static uint? FindActiveUserSessionId(string userName) => FindActiveUserSession(userName);

    private static uint? FindActiveUserSession(string userName)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return null;
        }

        try
        {
            var stride = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * stride));
                if (info.State != WtsConnectState.Active)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(userName))
                {
                    return (uint)info.SessionId;
                }

                if (!WTSQuerySessionInformation(IntPtr.Zero, (uint)info.SessionId, WtsInfoClass.WTSUserName, out var userPtr, out _))
                {
                    continue;
                }

                try
                {
                    var sessionUser = Marshal.PtrToStringUni(userPtr);
                    if (sessionUser is not null
                        && sessionUser.Equals(userName, StringComparison.OrdinalIgnoreCase))
                    {
                        return (uint)info.SessionId;
                    }
                }
                finally
                {
                    WTSFreeMemory(userPtr);
                }
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return null;
    }
}
