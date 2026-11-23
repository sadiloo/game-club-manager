using System;
using System.IO;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class WebViewProcessRecoveryExhaustedRetryTests
{
    [Fact]
    public void after_fast_budget_exhausted_backoff_stays_sixty_seconds_indefinitely()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(60), policy.GetBackoffForAttempt(10));
    }

    [Fact]
    public void exhausted_mode_still_allows_long_retries_in_policy()
    {
        var policy = new WebViewProcessRecoveryPolicy();
        var t0 = new System.DateTime(2026, 4, 2, 10, 0, 0, System.DateTimeKind.Utc);
        policy.RecordAttemptAndGetIndex(t0);
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(1));
        policy.RecordAttemptAndGetIndex(t0.AddSeconds(2));
        Assert.False(policy.CanRetryFast(t0.AddSeconds(3)));
        Assert.True(policy.CanRetry(t0.AddSeconds(3)));
    }

    [Fact]
    public void exhausted_offline_html_is_panel_page_not_blank()
    {
        var htmlPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../../src/GameClub.Shell/Assets/offline.html"));
        var html = File.ReadAllText(htmlPath);
        Assert.Contains("data-shell-offline-page=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("webview-recovery-exhausted", html, StringComparison.Ordinal);
        Assert.Contains("بازخوانی", html, StringComparison.Ordinal);
        Assert.Contains("webview-recovery-exhausted", html, StringComparison.Ordinal);
        var exhaustedIndex = html.IndexOf("webview-recovery-exhausted", StringComparison.Ordinal);
        var scriptTail = html[exhaustedIndex..];
        Assert.Contains("return;", scriptTail, StringComparison.Ordinal);
    }

}