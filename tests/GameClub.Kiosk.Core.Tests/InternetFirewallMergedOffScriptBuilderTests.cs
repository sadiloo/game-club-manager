using GameClub.Kiosk.Core.Guard;
using Xunit;

namespace GameClub.Kiosk.Core.Tests;

[Trait("Category", "PcInternetFirewall")]
public sealed class InternetFirewallMergedOffScriptBuilderTests
{
    private const string ExpectedDhcpRuleLine =
        "New-NetFirewallRule -DisplayName \"$group - DHCP\" -Group $group -Direction Outbound -Action Allow -Protocol UDP -LocalPort 68 -RemotePort 67 | Out-Null";

    [Fact]
    public void internet_off_apply_script_contains_single_dhcp_rule_matching_constant()
    {
        Assert.Equal(ExpectedDhcpRuleLine, InternetFirewallDhcpAllowRule.NewNetFirewallRuleInvocation);

        var script = InternetFirewallMergedOffScriptBuilder.BuildApplyOffScript(
            serverIps: ["203.0.113.10"],
            serverPort: 5225,
            extraAllowPrograms: []);

        Assert.DoesNotContain("67-68", script, StringComparison.Ordinal);
        Assert.DoesNotContain("67,68", script, StringComparison.Ordinal);

        var dhcpLines = script
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("New-NetFirewallRule", StringComparison.Ordinal)
                && line.Contains(" - DHCP", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal([ExpectedDhcpRuleLine], dhcpLines);
    }
}