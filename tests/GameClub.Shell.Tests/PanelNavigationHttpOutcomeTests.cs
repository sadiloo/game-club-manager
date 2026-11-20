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
    public void while_failing_http_zero_does_not_recover_but_successful_path_allows_zero()
    {
        Assert.False(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(true, true, true, 0));
        Assert.True(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(false, true, true, 0));
        Assert.True(PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(true, true, true, 200));
    }
}