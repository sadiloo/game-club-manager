namespace GameClub.Shell.Shell;

internal enum PanelFailingHiddenOfflinePresentAction
{
    SkipPresentOfflineInFlight,
    PresentPanelUnavailable,
}

/// <summary>
/// Poller branch when server is Running but panel load is failing and offline data page is not shown yet.
/// </summary>
internal static class RunningPanelLoadFailingPollerPolicy
{
    public static bool Applies(bool runningPanelLoadFailing, bool offlineShown)
        => runningPanelLoadFailing && !offlineShown;

    public static PanelFailingHiddenOfflinePresentAction ResolvePresentAction(string? offlinePresentInFlightState)
        => offlinePresentInFlightState is not null
            ? PanelFailingHiddenOfflinePresentAction.SkipPresentOfflineInFlight
            : PanelFailingHiddenOfflinePresentAction.PresentPanelUnavailable;
}