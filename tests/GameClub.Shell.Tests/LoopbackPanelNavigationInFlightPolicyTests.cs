using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class LoopbackPanelNavigationInFlightPolicyTests
{
    [Theory]
    [InlineData(5, true)]
    [InlineData(10, false)]
    [InlineData(0, true)]
    public void defers_only_while_oldest_pending_is_younger_than_deadline(double ageSec, bool expectedDefer) =>
        Assert.Equal(
            expectedDefer,
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
                hasPendingLoopbackPanelNavigation: true,
                oldestPendingAge: TimeSpan.FromSeconds(ageSec),
                pendingDeadline: TimeSpan.FromSeconds(10)));

    [Fact]
    public void does_not_defer_when_no_pending_loopback_navigation() =>
        Assert.False(
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
                hasPendingLoopbackPanelNavigation: false,
                oldestPendingAge: TimeSpan.FromSeconds(1),
                pendingDeadline: TimeSpan.FromSeconds(10)));
}