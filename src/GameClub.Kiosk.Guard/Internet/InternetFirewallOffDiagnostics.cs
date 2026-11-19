using System;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>CI/L17 firewall evidence lines (read-only).</summary>
public static class InternetFirewallOffDiagnostics
{
    public static void WriteL17OffApplyDiagnostics(InternetGuardConfig config, Action<string> writeLine)
        => InternetFirewallOffApplyReadback.WriteL17Diagnostics(config, writeLine);
}