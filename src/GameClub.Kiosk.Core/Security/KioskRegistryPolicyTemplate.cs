namespace GameClub.Kiosk.Core.Security;

/// <summary>Expected kiosk-user policy DWORD values (Policies\System and related).</summary>
public static class KioskRegistryPolicyTemplate
{
    public const string SystemPoliciesRelativePath = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ExplorerPoliciesRelativePath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string DisallowRunRelativePath = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\DisallowRun";

    public static IReadOnlyDictionary<string, int> SystemPolicies { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["DisableTaskMgr"] = 1,
        ["DisableLockWorkstation"] = 1,
        ["DisableChangePassword"] = 1,
        ["NoDispCPL"] = 1,
        ["NoCloseExplorer"] = 1,
        ["HideFastUserSwitching"] = 1,
    };

    public static IReadOnlyDictionary<string, int> ExplorerPolicies { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["NoLogoff"] = 1,
    };

    public static IReadOnlyList<string> DefaultDisallowRunExecutables { get; } =
    [
        "explorer.exe",
        "cmd.exe",
        "powershell.exe",
        "regedit.exe",
        "taskmgr.exe",
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
        "iexplore.exe",
    ];

    public static IReadOnlyList<string> ResolveDisallowRunEntries()
    {
        var extra = Environment.GetEnvironmentVariable("CHECKPOINT_DISALLOW_RUN");
        if (string.IsNullOrWhiteSpace(extra))
        {
            return DefaultDisallowRunExecutables;
        }

        var merged = new List<string>(DefaultDisallowRunExecutables);
        foreach (var raw in extra.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (!merged.Contains(part, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(part);
            }
        }

        return merged;
    }

    public static bool IsTampered(
        IReadOnlyDictionary<string, int?> systemActual,
        IReadOnlyDictionary<string, int?>? explorerActual = null,
        IReadOnlyList<string>? disallowRunActual = null)
    {
        foreach (var entry in SystemPolicies)
        {
            if (!systemActual.TryGetValue(entry.Key, out var value) || value != entry.Value)
            {
                return true;
            }
        }

        if (explorerActual is not null)
        {
            foreach (var entry in ExplorerPolicies)
            {
                if (!explorerActual.TryGetValue(entry.Key, out var value) || value != entry.Value)
                {
                    return true;
                }
            }
        }

        if (disallowRunActual is not null)
        {
            var expected = ResolveDisallowRunEntries();
            if (disallowRunActual.Count != expected.Count)
            {
                return true;
            }

            for (var i = 0; i < expected.Count; i++)
            {
                if (!string.Equals(disallowRunActual[i], expected[i], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool SidConfigured(string? sid) => !string.IsNullOrWhiteSpace(sid);
}
