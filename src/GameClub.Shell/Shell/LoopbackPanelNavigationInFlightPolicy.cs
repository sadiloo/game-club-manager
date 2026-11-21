using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// While a loopback panel navigation younger than the pending deadline is still in the
/// registry, defer another — not indefinitely (phantom pending must age out).
/// </summary>
internal static class LoopbackPanelNavigationInFlightPolicy
{
    public static bool ShouldDeferNewPanelNavigation(
        bool hasPendingLoopbackPanelNavigation,
        TimeSpan oldestPendingAge,
        TimeSpan pendingDeadline) =>
        hasPendingLoopbackPanelNavigation && oldestPendingAge < pendingDeadline;
}