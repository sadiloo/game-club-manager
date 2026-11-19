using GameClub.Kiosk.Core.Guard;
using Xunit;

namespace GameClub.Kiosk.Core.Tests;

[Trait("Category", "PcInternetFirewall")]
public sealed class PcInternetFirewallOffVerificationTests
{
    private static readonly bool[] BoolValues = [false, true];

    [Fact]
    public void internet_off_applied_requires_active_and_localhost_profiles_block()
    {
        Assert.Equal(
            "apply_failed:verification:active_profiles_not_block",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: false,
                allLocalhostProfilesBlock: true,
                guardRuleCount: 2,
                stateFilePresent: true,
                requestedOn: false));
    }

    [Fact]
    public void internet_off_applied_requires_localhost_profiles_block()
    {
        Assert.Equal(
            "apply_failed:verification:localhost_profiles_not_block",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: false,
                guardRuleCount: 2,
                stateFilePresent: true,
                requestedOn: false));
    }

    [Fact]
    public void internet_off_applied_requires_guard_rule_group()
    {
        Assert.Equal(
            "apply_failed:verification:missing_checkpoint_off_rules",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                guardRuleCount: 0,
                stateFilePresent: true,
                requestedOn: false));
    }

    [Fact]
    public void internet_off_applied_requires_verified_state_file()
    {
        Assert.Equal(
            "apply_failed:verification:state_missing",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                guardRuleCount: 2,
                stateFilePresent: false,
                requestedOn: false));
    }

    [Fact]
    public void internet_off_applied_succeeds_only_when_every_gate_passes()
    {
        Assert.Null(
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                guardRuleCount: 2,
                stateFilePresent: true,
                requestedOn: false));
    }

    [Theory]
    [MemberData(nameof(OnToggleCases))]
    public void internet_on_path_skips_off_verification_gates(bool requestedOn)
    {
        if (requestedOn)
        {
            Assert.Null(
                InternetFirewallOffVerification.TryGetApplyFailedReason(
                    allActiveProfilesBlock: false,
                    allLocalhostProfilesBlock: false,
                    guardRuleCount: 0,
                    stateFilePresent: false,
                    requestedOn: true));
        }
    }

    public static TheoryData<bool> OnToggleCases()
    {
        var data = new TheoryData<bool>();
        foreach (var v in BoolValues)
        {
            data.Add(v);
        }

        return data;
    }
}