using GameClub.Kiosk.Core.Security;
using Microsoft.Win32;

namespace GameClub.Kiosk.Guard;

internal static class RegistryPolicyStore
{
    private const string SystemPoliciesPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string ExplorerPoliciesPath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    internal static void EnsureDefaults()
    {
        using var systemKey = OpenKioskHive(SystemPoliciesPath, writable: true);
        if (systemKey is not null)
        {
            foreach (var entry in KioskRegistryPolicyTemplate.SystemPolicies)
            {
                systemKey.SetValue(entry.Key, entry.Value, RegistryValueKind.DWord);
            }
        }

        using var explorerKey = OpenKioskHive(ExplorerPoliciesPath, writable: true);
        if (explorerKey is not null)
        {
            explorerKey.SetValue("DisallowRun", 1, RegistryValueKind.DWord);
            foreach (var entry in KioskRegistryPolicyTemplate.ResolveDisallowRunEntries())
            {
                explorerKey.SetValue(entry, 1, RegistryValueKind.String);
            }
        }
    }

    internal static bool Tampered()
    {
        using var key = OpenKioskHive(SystemPoliciesPath, writable: false);
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

    private static RegistryKey? OpenKioskHive(string relativePath, bool writable)
    {
        var sid = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_SID")?.Trim();
        if (!KioskRegistryPolicyTemplate.SidConfigured(sid))
        {
            return null;
        }

        var fullPath = $"{sid}\\{relativePath}";
        return writable
            ? Registry.Users.CreateSubKey(fullPath, true)
            : Registry.Users.OpenSubKey(fullPath, writable: false);
    }
}
