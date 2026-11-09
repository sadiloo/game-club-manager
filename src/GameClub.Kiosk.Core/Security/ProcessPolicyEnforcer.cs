using System.Diagnostics;

namespace GameClub.Kiosk.Core.Security;

/// <summary>
/// Closes processes that are not on the allowlist while the kiosk shell is locked or playing.
/// </summary>
public sealed class ProcessPolicyEnforcer
{
    public static readonly string[] FreezeDenyProcessNames =
    [
        "explorer", "cmd", "powershell", "regedit", "taskmgr", "chrome", "msedge", "firefox", "iexplore",
    ];

    public bool IsProcessAllowed(string processName, IReadOnlyCollection<string> allowedProcessNames)
    {
        var allow = BuildAllowSet(allowedProcessNames);
        if (allow.Contains(processName))
        {
            return true;
        }

        return allowedProcessNames.Any(a => processName.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public void EnforceAllowlist(IReadOnlyCollection<string> allowedProcessNames)
    {
        var allow = BuildAllowSet(allowedProcessNames);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (allow.Contains(name) || IsProcessAllowed(name, allowedProcessNames))
                {
                    continue;
                }

                process.Kill();
            }
            catch
            {
                // ignore access denied on protected processes
            }
        }
    }

    public void EnforceDenyList(IReadOnlyCollection<string> denyNames)
    {
        var deny = new HashSet<string>(denyNames, StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (deny.Contains(process.ProcessName))
                {
                    process.Kill();
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static HashSet<string> BuildAllowSet(IReadOnlyCollection<string> allowedProcessNames)
    {
        var allow = new HashSet<string>(allowedProcessNames, StringComparer.OrdinalIgnoreCase);
        allow.Add("gameclub.kiosk");
        allow.Add("steam");
        allow.Add("steamwebhelper");
        return allow;
    }
}
