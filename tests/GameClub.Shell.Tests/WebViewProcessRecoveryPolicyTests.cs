using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class WebViewProcessRecoveryPolicyTests
{
    [Fact]
    public void process_recovery_backoff_follows_one_five_fifteen_seconds()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        Assert.Equal(WebViewProcessRecoveryBackoff.Delay1, policy.GetBackoffForAttempt(1));
        Assert.Equal(WebViewProcessRecoveryBackoff.Delay2, policy.GetBackoffForAttempt(2));
        Assert.Equal(WebViewProcessRecoveryBackoff.Delay3, policy.GetBackoffForAttempt(3));
        Assert.Null(policy.GetBackoffForAttempt(4));
    }

    [Fact]
    public void process_recovery_allows_at_most_three_attempts_per_five_minute_window()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(policy.CanRetry(t0));
        Assert.Equal(1, policy.RecordAttemptAndGetIndex(t0));
        Assert.True(policy.CanRetry(t0.AddSeconds(1)));
        Assert.Equal(2, policy.RecordAttemptAndGetIndex(t0.AddSeconds(1)));
        Assert.True(policy.CanRetry(t0.AddSeconds(2)));
        Assert.Equal(3, policy.RecordAttemptAndGetIndex(t0.AddSeconds(2)));
        Assert.False(policy.CanRetry(t0.AddSeconds(3)));
        Assert.Equal(3, policy.AttemptCountInWindow(t0.AddSeconds(3)));
    }

    [Fact]
    public void process_recovery_prunes_attempts_older_than_five_minutes()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        policy.RecordAttemptAndGetIndex(t0);
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(1));
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(2));
        Assert.False(policy.CanRetry(t0.AddSeconds(3)));
        var afterWindow = t0.Add(WebViewProcessRecoveryBackoff.RollingWindow).AddSeconds(5);
        Assert.Equal(0, policy.AttemptCountInWindow(afterWindow));
        Assert.True(policy.CanRetry(afterWindow));
    }
}