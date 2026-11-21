using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class LoopbackPanelNavigationInFlightPolicyTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void defers_only_when_loopback_panel_navigation_is_pending(bool hasPending, bool expectedDefer) =>
        Assert.Equal(
            expectedDefer,
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(hasPending));
}