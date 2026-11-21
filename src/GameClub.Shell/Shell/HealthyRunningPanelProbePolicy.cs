using System;

namespace GameClub.Shell.Shell;

/// <summary>
/// When the panel is already on the loopback operator origin, HTTP probe-only ticks
/// detect transient panel failures without issuing another panel navigation.
/// </summary>
internal static class HealthyRunningPanelProbePolicy
{
    public static readonly TimeSpan DispatcherPollInterval = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan PerProbeTimeout = TimeSpan.FromSeconds(1);

    public const int ConsecutiveFailuresForConfirmation = 2;

    public static bool ShouldProbeOnPollerTick(
        bool runningPanelLoadFailing,
        bool panelNavigated,
        bool activeOnPanelOrigin)
        => !runningPanelLoadFailing && panelNavigated && activeOnPanelOrigin;
}