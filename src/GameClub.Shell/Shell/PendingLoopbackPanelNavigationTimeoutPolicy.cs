using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// Pending loopback panel navigations that exceed this deadline are confirmed timeout failures (T1).
/// </summary>
internal static class PendingLoopbackPanelNavigationTimeoutPolicy
{
    public static readonly TimeSpan Deadline = PanelNavigationRetryBackoff.Delay2;
}