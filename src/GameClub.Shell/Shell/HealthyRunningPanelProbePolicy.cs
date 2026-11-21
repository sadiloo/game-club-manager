using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// When the panel is already on the loopback operator origin, HTTP probe-only ticks
/// detect transient panel failures without issuing another panel navigation.
/// </summary>
internal static class HealthyRunningPanelProbePolicy
{
    /// <summary>
    /// Faster than the 5s service poller so transient probe failures surface offline
    /// within one B-1 sample interval (500ms).
    /// </summary>
    public static readonly TimeSpan DispatcherPollInterval = TimeSpan.FromMilliseconds(500);

    public static bool ShouldProbeOnPollerTick(
        bool runningPanelLoadFailing,
        bool panelNavigated,
        bool activeOnPanelOrigin)
        => !runningPanelLoadFailing && panelNavigated && activeOnPanelOrigin;
}