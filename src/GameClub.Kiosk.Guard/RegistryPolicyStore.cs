using GameClub.Kiosk.Core.Security;
using Microsoft.Win32;

namespace GameClub.Kiosk.Guard;

internal static class RegistryPolicyStore
{
    internal static void EnsureDefaults()
    {
        using var systemKey = OpenKioskHive(KioskRegistryPolicyTemplate.SystemPoliciesRelativePath, writable: true);
        if (systemKey is not null)
        {
            foreach (var entry in KioskRegistryPolicyTemplate.SystemPolicies)
            {
                systemKey.SetValue(entry.Key, entry.Value, RegistryValueKind.DWord);
            }
        }

        using var explorerKey = OpenKioskHive(KioskRegistryPolicyTemplate.ExplorerPoliciesRelativePath, writable: true);
        if (explorerKey is not null)
        {
            foreach (var entry in KioskRegistryPolicyTemplate.ExplorerPolicies)
            {
                explorerKey.SetValue(entry.Key, entry.Value, RegistryValueKind.DWord);
            }
        }

        using var disallowKey = OpenKioskHive(KioskRegistryPolicyTemplate.DisallowRunRelativePath, writable: true);
        if (disallowKey is not null)
        {
            var entries = KioskRegistryPolicyTemplate.ResolveDisallowRunEntries();
            for (var i = 0; i < entries.Count; i++)
            {
                disallowKey.SetValue((i + 1).ToString(), entries[i], RegistryValueKind.String);
            }
        }
    }

    internal static bool Tampered()
    {
        using var systemKey = OpenKioskHive(KioskRegistryPolicyTemplate.SystemPoliciesRelativePath, writable: false);
        if (systemKey is null)
        {
            return true;
        }

        var systemActual = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in KioskRegistryPolicyTemplate.SystemPolicies.Keys)
        {
            systemActual[name] = systemKey.GetValue(name) as int?;
        }

        Dictionary<string, int?>? explorerActual = null;
        using (var explorerKey = OpenKioskHive(KioskRegistryPolicyTemplate.ExplorerPoliciesRelativePath, writable: false))
        {
            if (explorerKey is null)
            {
                return true;
            }

            explorerActual = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in KioskRegistryPolicyTemplate.ExplorerPolicies.Keys)
            {
                explorerActual[name] = explorerKey.GetValue(name) as int?;
            }
        }

        DisallowRunPolicySnapshot disallowSnapshot;
        using (var disallowKey = OpenKioskHive(KioskRegistryPolicyTemplate.DisallowRunRelativePath, writable: false))
        {
            if (disallowKey is null)
            {
                disallowSnapshot = DisallowRunPolicySnapshot.MissingChild;
            }
            else
            {
                var disallowActual = new List<string>();
                var index = 1;
                while (true)
                {
                    var value = disallowKey.GetValue(index.ToString()) as string;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        break;
                    }

                    disallowActual.Add(value!);
                    index++;
                    if (index > 64)
                    {
                        break;
                    }
                }

                disallowSnapshot = DisallowRunPolicySnapshot.WithEntries(disallowActual);
            }
        }

        return KioskRegistryPolicyTemplate.IsTampered(systemActual!, explorerActual!, disallowSnapshot);
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
