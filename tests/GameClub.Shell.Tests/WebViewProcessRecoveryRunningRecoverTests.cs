using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class WebViewProcessRecoveryRunningRecoverTests
{
    [Fact]
    public void running_recover_attempts_share_process_recovery_fast_budget()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        var t0 = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);

        policy.RecordAttemptAndGetIndex(t0);
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(1));
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(2));

        Assert.False(policy.CanRetryFast(t0.AddSeconds(3)));
        Assert.Equal(3, policy.AttemptCountInWindow(t0.AddSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(4));
        Assert.True(policy.CanRetry(t0.AddSeconds(3)));
    }

    [Fact]
    public void mixed_process_failures_and_running_recover_exhaust_then_use_sixty_second_retry()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        var t0 = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
        policy.RecordAttemptAndGetIndex(t0);
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(2));
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(4));

        Assert.False(policy.CanRetryFast(t0.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(5));
    }
}