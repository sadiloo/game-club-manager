namespace GameClub.Kiosk.Core.Security;

/// <summary>Expected kiosk-user policy DWORD values (Policies\System and related).</summary>
public static class KioskRegistryPolicyTemplate
{
    public static IReadOnlyDictionary<string, int> SystemPolicies { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["DisableTaskMgr"] = 1,
        ["DisableLockWorkstation"] = 1,
        ["DisableChangePassword"] = 1,
        ["NoDispCPL"] = 1,
        ["NoCloseExplorer"] = 1,
    };

    public static bool IsTampered(IReadOnlyDictionary<string, int?> actual)
    {
        foreach (var entry in SystemPolicies)
        {
            if (!actual.TryGetValue(entry.Key, out var value) || value != entry.Value)
            {
                return true;
            }
        }

        return false;
    }

    public static bool SidConfigured(string? sid) => !string.IsNullOrWhiteSpace(sid);
}
