using GameClub.Kiosk.Core.Security;
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

        foreach (var entry in KioskRegistryPolicyTemplate.SystemPolicies)
        {
            key.SetValue(entry.Key, entry.Value, RegistryValueKind.DWord);
        }
    }

    internal static bool Tampered()
    {
        using var key = OpenKioskHive(writable: false);
        if (key is null)
        {
            return true;
        }

        var actual = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in KioskRegistryPolicyTemplate.SystemPolicies.Keys)
        {
            actual[name] = key.GetValue(name) as int?;
        }

        return KioskRegistryPolicyTemplate.IsTampered(actual);
    }

    internal static void Repair() => EnsureDefaults();

    private static RegistryKey? OpenKioskHive(bool writable)
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (!KioskRegistryPolicyTemplate.SidConfigured(sid))
        {
            return null;
        }

        var fullPath = $"{sid}\\{PoliciesPath}";
        return writable
            ? Registry.Users.CreateSubKey(fullPath, true)
            : Registry.Users.OpenSubKey(fullPath, writable: false);
    }
}
