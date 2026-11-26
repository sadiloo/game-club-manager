using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// A loopback panel navigation that has been pending longer than the deadline is a confirmed
/// timeout failure (T1), not a silent registry prune.
/// </summary>
internal static class PendingLoopbackPanelNavigationExpiryPolicy
{
    public static bool HasReachedDeadline(TimeSpan oldestPendingAge, TimeSpan deadline) =>
        oldestPendingAge >= deadline;
}