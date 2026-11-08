using System.Diagnostics;

namespace GameClub.Kiosk.Core.Security;

/// <summary>
/// Closes processes that are not on the allowlist while the kiosk shell is locked or playing.
/// </summary>
public sealed class ProcessPolicyEnforcer
{
    private static readonly string[] DefaultDeny =
    [
        "explorer", "cmd", "powershell", "regedit", "taskmgr", "chrome", "msedge", "firefox",
    ];

    public void EnforceAllowlist(IReadOnlyCollection<string> allowedProcessNames)
    {
        var allow = new HashSet<string>(allowedProcessNames, StringComparer.OrdinalIgnoreCase);
        allow.Add("gameclub.kiosk");
        allow.Add("steam");
        allow.Add("steamwebhelper");
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (allow.Contains(name) || allowedProcessNames.Any(a => name.IndexOf(a, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (DefaultDeny.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    process.Kill();
                }
            }
            catch
            {
                // ignore access denied on protected processes
            }
        }
    }
}
