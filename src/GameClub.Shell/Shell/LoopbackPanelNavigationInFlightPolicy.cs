namespace GameClub.Shell.Shell;

/// <summary>
/// While a loopback panel navigation is still pending, do not start another — retries
/// supersede in-flight loads and recovery never settles.
/// </summary>
internal static class LoopbackPanelNavigationInFlightPolicy
{
    public static bool ShouldDeferNewPanelNavigation(bool hasPendingLoopbackPanelNavigation) =>
        hasPendingLoopbackPanelNavigation;
}