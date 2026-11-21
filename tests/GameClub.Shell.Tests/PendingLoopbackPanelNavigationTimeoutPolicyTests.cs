using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PendingLoopbackPanelNavigationTimeoutPolicyTests
{
    [Fact]
    public void expires_when_pending_loopback_panel_navigation_reaches_deadline_without_offline()
    {
        Assert.True(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            runningPanelLoadFailing: false,
            offlineShown: false,
            timeoutCycleConsumed: false,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void does_not_expire_when_probe_would_be_healthy_but_age_below_deadline()
    {
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            runningPanelLoadFailing: false,
            offlineShown: false,
            timeoutCycleConsumed: false,
            hasPendingLoopbackNavigation: true,
            oldestPendingAge: TimeSpan.FromSeconds(9.9)));
    }

    [Fact]
    public void does_not_expire_after_cycle_consumed_or_offline_already_shown()
    {
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            false, false, true, true, TimeSpan.FromSeconds(15)));
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            false, true, false, true, TimeSpan.FromSeconds(15)));
        Assert.False(PendingLoopbackPanelNavigationTimeoutPolicy.ShouldExpireInFlightNavigation(
            true, false, false, true, TimeSpan.FromSeconds(15)));
    }
}