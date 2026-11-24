using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationRetryPolicyTests
{
    [Fact]
    public void panel_navigation_retry_backoff_follows_five_ten_thirty_then_thirty_seconds()
    {
        var policy = new PanelNavigationRetryPolicy();
        Assert.Equal(PanelNavigationRetryBackoff.Delay1, policy.GetDelayAfterFailure(1));
        Assert.Equal(PanelNavigationRetryBackoff.Delay2, policy.GetDelayAfterFailure(2));
        Assert.Equal(PanelNavigationRetryBackoff.Delay3, policy.GetDelayAfterFailure(3));
        Assert.Equal(PanelNavigationRetryBackoff.DelayRepeat, policy.GetDelayAfterFailure(4));
        Assert.Equal(PanelNavigationRetryBackoff.DelayRepeat, policy.GetDelayAfterFailure(99));
    }

    [Fact]
    public void panel_navigation_retry_blocks_attempts_until_backoff_elapses()
    {
        var policy = new PanelNavigationRetryPolicy();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(policy.CanAttempt(t0));
        policy.RecordFailure(t0);
        Assert.False(policy.CanAttempt(t0.AddSeconds(4)));
        Assert.True(policy.CanAttempt(t0.AddSeconds(5)));
        policy.RecordFailure(t0.AddSeconds(5));
        Assert.False(policy.CanAttempt(t0.AddSeconds(14)));
        Assert.True(policy.CanAttempt(t0.AddSeconds(15)));
    }

    [Fact]
    public void panel_navigation_retry_reset_clears_failures()
    {
        var policy = new PanelNavigationRetryPolicy();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        policy.RecordFailure(t0);
        policy.Reset();
        Assert.Equal(0, policy.FailureCount);
        Assert.True(policy.CanAttempt(t0));
    }
}