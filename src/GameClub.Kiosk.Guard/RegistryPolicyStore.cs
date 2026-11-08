using Microsoft.Win32;

namespace GameClub.Kiosk.Guard;

internal static class RegistryPolicyStore
{
    private const string PoliciesPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";

    internal static void EnsureDefaults()
    {
        using var key = Registry.CurrentUser.CreateSubKey(PoliciesPath, true);
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
        using var key = Registry.CurrentUser.OpenSubKey(PoliciesPath);
        if (key is null)
        {
            return true;
        }

        return key.GetValue("DisableTaskMgr") is not int v || v != 1;
    }

    internal static void Repair() => EnsureDefaults();
}
