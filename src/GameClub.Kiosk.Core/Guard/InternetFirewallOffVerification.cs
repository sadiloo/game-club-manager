namespace GameClub.Kiosk.Core.Guard;

/// <summary>Classifies whether internet-off apply may return <c>applied</c> (brief §10 / PR40-R15).</summary>
public static class InternetFirewallOffVerification
{
    public static string? TryGetApplyFailedReason(
        bool allActiveProfilesBlock,
        bool anyLocalhostProfileBlock,
        int activeStoreGuardRuleCount,
        bool checkpointServerReachable,
        bool stateFilePresent,
        bool requestedOn,
        bool requireVerifiedStateFile)
    {
        if (requestedOn)
        {
            return null;
        }

        if (!allActiveProfilesBlock)
        {
            return "apply_failed:verification:active_profiles_not_block";
        }

        if (anyLocalhostProfileBlock)
        {
            return "apply_failed:verification:localhost_profiles_still_block";
        }

        if (activeStoreGuardRuleCount < 1)
        {
            return "apply_failed:verification:missing_checkpoint_off_rules";
        }

        if (!checkpointServerReachable)
        {
            return "apply_failed:verification:checkpoint_server_unreachable";
        }

        if (requireVerifiedStateFile && !stateFilePresent)
        {
            return "apply_failed:verification:state_missing";
        }

        return null;
    }

    public static string? TryGetOnResetFailedReason(
        bool anyActiveProfileStillBlock,
        int activeStoreGuardRuleCount)
    {
        if (anyActiveProfileStillBlock)
        {
            return "apply_failed:verification:active_profiles_still_block";
        }

        if (activeStoreGuardRuleCount > 0)
        {
            return "apply_failed:verification:checkpoint_off_rules_remain";
        }

        return null;
    }
}