namespace GameClub.Kiosk.Core.Guard;

/// <summary>DHCP client discover/request during internet-off: UDP local 68 → remote 67 only (brief N-6).</summary>
public static class InternetFirewallDhcpAllowRule
{
    public const string NewNetFirewallRuleInvocation =
        "New-NetFirewallRule -DisplayName \"$group - DHCP\" -Group $group -Direction Outbound -Action Allow -Protocol UDP -LocalPort 68 -RemotePort 67 | Out-Null";
}