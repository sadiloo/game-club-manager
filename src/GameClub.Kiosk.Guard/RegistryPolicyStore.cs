using Microsoft.Win32;

namespace GameClub.Kiosk.Guard;

internal static class RegistryPolicyStore
{
    private const string PoliciesPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    internal static void EnsureDefaults()
    {
        using var key = OpenKioskHive(writable: true);
        if (key is null)
        {
            return;
        }

        key.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord);
        key.SetValue("DisableLockWorkstation", 1, RegistryValueKind.DWord);
        key.SetValue("DisableChangePassword", 1, RegistryValueKind.DWord);
    }

    internal static bool Tampered()
    {
        using var key = OpenKioskHive(writable: false);
        if (key is null)
        {
            return true;
        }

        return key.GetValue("DisableTaskMgr") is not int v || v != 1;
    }

    internal static void Repair() => EnsureDefaults();

    private static RegistryKey? OpenKioskHive(bool writable)
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID");
        if (!string.IsNullOrWhiteSpace(sid))
        {
            var fullPath = $"{sid.Trim()}\\{PoliciesPath}";
            return writable
                ? Registry.Users.CreateSubKey(fullPath, true)
                : Registry.Users.OpenSubKey(fullPath, writable: false);
        }

        return writable
            ? Registry.CurrentUser.CreateSubKey(PoliciesPath, true)
            : Registry.CurrentUser.OpenSubKey(PoliciesPath, writable: false);
    }
}
