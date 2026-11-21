using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// Probe health does not gate panel navigation timeout: pending loopback panel
/// navigations that exceed the deadline are navigation failures.
/// </summary>
internal static class PendingLoopbackPanelNavigationTimeoutPolicy
{
    public static readonly TimeSpan Deadline = PanelNavigationRetryBackoff.Delay2;

    public static bool ShouldExpireInFlightNavigation(
        bool timeoutCycleConsumed,
        bool hasPendingLoopbackNavigation,
        TimeSpan oldestPendingAge)
    {
        if (timeoutCycleConsumed || !hasPendingLoopbackNavigation)
        {
            return false;
        }

        return oldestPendingAge >= Deadline;
    }
}