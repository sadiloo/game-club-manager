namespace GameClub.Shell.Shell;

/// <summary>
/// When the panel is already on the loopback operator origin, HTTP probe-only ticks
/// detect transient panel failures without issuing another panel navigation.
/// </summary>
internal static class HealthyRunningPanelProbePolicy
{
    public static bool ShouldProbeOnPollerTick(
        bool runningPanelLoadFailing,
        bool panelNavigated,
        bool activeOnPanelOrigin)
        => !runningPanelLoadFailing && panelNavigated && activeOnPanelOrigin;
}