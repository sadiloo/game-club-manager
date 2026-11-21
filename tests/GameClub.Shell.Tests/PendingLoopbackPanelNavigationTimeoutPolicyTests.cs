using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PendingLoopbackPanelNavigationTimeoutPolicyTests
{
    [Fact]
    public void expires_when_pending_loopback_panel_navigation_reaches_deadline()
    {
        Assert.True(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            timeoutCycleConsumed: false,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void does_not_expire_when_age_below_deadline()
    {
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            timeoutCycleConsumed: false,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(9.9)));
    }

    [Fact]
    public void does_not_expire_after_cycle_consumed()
    {
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            timeoutCycleConsumed: true,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void still_expires_stale_pending_even_when_offline_would_have_blocked_old_gate()
    {
        Assert.True(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            timeoutCycleConsumed: false,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(10.5)));
    }
}