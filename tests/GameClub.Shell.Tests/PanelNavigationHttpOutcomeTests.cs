using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationHttpOutcomeTests
{
    [Fact]
    public void confirmed_recovery_requires_2xx_not_http_zero()
    {
        Assert.False(PanelNavigationHttpOutcome.IsConfirmedPanelHttpRecovery(true, true, 0));
        Assert.True(PanelNavigationHttpOutcome.IsConfirmedPanelHttpRecovery(true, true, 200));
        Assert.True(PanelNavigationHttpOutcome.IsConfirmedPanelHttpRecovery(true, true, 299));
        Assert.False(PanelNavigationHttpOutcome.IsConfirmedPanelHttpRecovery(true, true, 503));
    }

    [Fact]
    public void http_zero_navigation_never_counts_as_panel_recovery()
    {
        Assert.False(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(true, true, true, 0));
        Assert.False(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(false, true, true, 0));
        Assert.False(PanelNavigationHttpOutcome.IsSuccessfulPanelHttpNavigation(true, true, 0));
        Assert.True(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(true, true, true, 200));
    }
}