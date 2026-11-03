namespace GameClub.Kiosk.Core;

public sealed record ProcessDecision(bool Kill, string Reason);

/// <summary>
/// Deny-list for known desktop escapes. Everything else is left running, including Windows
/// services, anti-cheat, and GPU helpers. Listed games and their launchers are labeled
/// <c>catalog</c> so the guard has an explicit allow. A forbidden name still loses: a catalog
/// entry cannot whitelist <c>cmd</c>.
/// </summary>
public static class ProcessPolicy
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "taskmgr",
        "cmd",
        "powershell",
        "pwsh",
        "powershell_ise",
        "regedit",
        "reg",
        "mmc",
        "control",
        "msconfig",
        "utilman",
        "sethc",
        "wscript",
        "cscript",
        "mshta",
        "wt",
        "taskkill",
        "systemsettings",
        "cmdkey",
    };

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "gameclub.kiosk",
        "gameclub.kiosk.guard",
    };

    public static ProcessDecision Evaluate(string processName, bool elevated, IEnumerable<string>? catalogProcesses)
    {
        var name = Normalize(processName);
        if (name.Length == 0)
        {
            return new ProcessDecision(false, "unnamed");
        }

        if (Protected.Contains(name))
        {
            return new ProcessDecision(false, "kiosk");
        }

        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            return elevated
                ? new ProcessDecision(false, "admin_explorer")
                : new ProcessDecision(true, "explorer");
        }

        if (Forbidden.Contains(name))
        {
            return new ProcessDecision(true, "forbidden");
        }

        if (catalogProcesses is not null)
        {
            foreach (var allowed in catalogProcesses)
            {
                if (string.Equals(Normalize(allowed), name, StringComparison.OrdinalIgnoreCase))
                {
                    return new ProcessDecision(false, "catalog");
                }
            }
        }

        return new ProcessDecision(false, "not_forbidden");
    }

    public static string Normalize(string? processName)
    {
        if (processName is null)
        {
            return "";
        }

        var file = processName.Trim();
        if (file.Length == 0)
        {
            return "";
        }
        var slash = Math.Max(file.LastIndexOf('\\'), file.LastIndexOf('/'));
        if (slash >= 0 && slash < file.Length - 1)
        {
            file = file.Substring(slash + 1);
        }

        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            file = file.Substring(0, file.Length - 4);
        }

        return file;
    }
}
