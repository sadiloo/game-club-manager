using GameClub.Kiosk.Core.Guard;
using Xunit;

namespace GameClub.Kiosk.Core.Tests;

[Trait("Category", "PcInternetFirewall")]
public sealed class PcInternetFirewallOffVerificationTests
{
    private static readonly bool[] BoolValues = [false, true];

    [Fact]
    public void internet_off_applied_requires_active_profiles_block()
    {
        Assert.Equal(
            "apply_failed:verification:active_profiles_not_block",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: false,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: true,
                stateFilePresent: true,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_applied_requires_localhost_profiles_block()
    {
        Assert.Equal(
            "apply_failed:verification:localhost_profiles_not_block",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: false,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: true,
                stateFilePresent: true,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_applied_requires_active_store_guard_rule_group()
    {
        Assert.Equal(
            "apply_failed:verification:missing_checkpoint_off_rules",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 0,
                checkpointServerReachable: true,
                stateFilePresent: true,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_applied_requires_checkpoint_server_reachable()
    {
        Assert.Equal(
            "apply_failed:verification:checkpoint_server_unreachable",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: false,
                stateFilePresent: true,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_applied_requires_verified_state_file_when_required()
    {
        Assert.Equal(
            "apply_failed:verification:state_missing",
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: true,
                stateFilePresent: false,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_applied_succeeds_when_every_gate_passes_and_state_required()
    {
        Assert.Null(
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: true,
                stateFilePresent: true,
                requestedOn: false,
                requireVerifiedStateFile: true));
    }

    [Fact]
    public void internet_off_firewall_layer_skips_state_until_guard_persists_command()
    {
        Assert.Null(
            InternetFirewallOffVerification.TryGetApplyFailedReason(
                allActiveProfilesBlock: true,
                allLocalhostProfilesBlock: true,
                activeStoreGuardRuleCount: 2,
                checkpointServerReachable: true,
                stateFilePresent: false,
                requestedOn: false,
                requireVerifiedStateFile: false));
    }

    [Fact]
    public void readback_model_r14_false_applied_runs_classifier_for_active_store_only()
    {
        const string json =
            """
            {"activeOut":["NotConfigured","NotConfigured","NotConfigured"],"localOut":["Block","Block","Block"],"activeRuleCount":0,"localRuleCount":2,"checkpointServerReachable":true,"stateFilePresent":false}
            """;
        Assert.True(
            InternetFirewallOffReadbackModel.TryParseDiagnosticJson(json, out var model, out _, out _));
        Assert.Equal("apply_failed:verification:active_profiles_not_block", model.ClassifyOff(requireVerifiedStateFile: false));
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
                    activeStoreGuardRuleCount: 0,
                    checkpointServerReachable: false,
                    stateFilePresent: false,
                    requestedOn: true,
                    requireVerifiedStateFile: true));
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