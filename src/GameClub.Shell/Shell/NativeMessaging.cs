using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameClub.Shell.Shell;

internal static class NativeMessaging
{
    private const int WM_SHOW_SHELL = 0x8001;

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static void SignalExistingInstance()
    {
        foreach (var process in Process.GetProcessesByName("GameClub.Shell"))
        {
            if (process.Id == Process.GetCurrentProcess().Id)
            {
                continue;
            }

            var handle = process.MainWindowHandle;
            if (handle != IntPtr.Zero)
            {
                PostMessage(handle, WM_SHOW_SHELL, IntPtr.Zero, IntPtr.Zero);
            }

            break;
        }
    }
}