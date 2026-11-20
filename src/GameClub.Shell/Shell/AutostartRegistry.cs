using Microsoft.Win32;

namespace GameClub.Shell.Shell;

internal static class AutostartRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ShellPaths.AutostartValueName) is string;
        }
    }

    public static void EnsureDefaultOn()
    {
        if (!IsEnabled)
        {
            SetEnabled(true);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            key.SetValue(ShellPaths.AutostartValueName, "\"" + exe + "\"");
        }
        else
        {
            key.DeleteValue(ShellPaths.AutostartValueName, false);
        }
    }
}