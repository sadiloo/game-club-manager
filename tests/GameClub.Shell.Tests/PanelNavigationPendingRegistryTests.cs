using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationPendingRegistryTests
{
    private static bool IsLoopbackPanel(string uri) =>
        uri.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || uri.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void oldest_pending_loopback_panel_navigation_tracks_age_by_start_time()
    {
        var registry = new PanelNavigationPendingRegistry();
        var t0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        registry.RecordNavigationStarting(1, "data:text/html,offline", t0);
        registry.RecordNavigationStarting(2, "http://127.0.0.1:5000/operator/", t0.AddSeconds(-12));
        registry.RecordNavigationStarting(3, "http://127.0.0.1:5000/operator/other", t0.AddSeconds(-5));

        Assert.True(registry.TryGetOldestPendingLoopbackPanelNavigation(
            IsLoopbackPanel,
            t0,
            out var age,
            out var id));
        Assert.Equal(2ul, id);
        Assert.Equal(TimeSpan.FromSeconds(12), age);
    }
    [Fact]
    public void orphan_navigation_id_completion_is_ignored_and_does_not_remove_other_entries()
    {
        var registry = new PanelNavigationPendingRegistry();
        registry.RecordNavigationStarting(1, "http://127.0.0.1:5000/operator/", DateTime.UtcNow);
        registry.RecordNavigationStarting(2, "http://127.0.0.1:5000/operator/other", DateTime.UtcNow);

        Assert.False(registry.TryTakeNavigationCompleted(99, out _));
        Assert.Equal(2, registry.PendingCount);

        Assert.True(registry.TryTakeNavigationCompleted(2, out var uri));
        Assert.Equal("http://127.0.0.1:5000/operator/other", uri);
        Assert.Equal(1, registry.PendingCount);
        Assert.True(registry.TryTakeNavigationCompleted(1, out uri));
        Assert.Equal("http://127.0.0.1:5000/operator/", uri);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void shared_operator_prefix_navigations_match_by_navigation_id_not_prefix()
    {
        var registry = new PanelNavigationPendingRegistry();
        registry.RecordNavigationStarting(10, "http://127.0.0.1:5000/operator/index.html", DateTime.UtcNow);
        registry.RecordNavigationStarting(11, "http://127.0.0.1:5000/operator/second", DateTime.UtcNow);

        Assert.True(registry.TryTakeNavigationCompleted(10, out var first));
        Assert.True(registry.TryTakeNavigationCompleted(11, out var second));
        Assert.Equal("http://127.0.0.1:5000/operator/index.html", first);
        Assert.Equal("http://127.0.0.1:5000/operator/second", second);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void missing_navigation_completed_ages_out_at_deadline_so_new_panel_nav_is_not_deferred()
    {
        var registry = new PanelNavigationPendingRegistry();
        var t0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        registry.RecordNavigationStarting(7, "http://127.0.0.1:5000/operator/", t0);
        var deadline = TimeSpan.FromSeconds(10);
        var tExpire = t0.AddSeconds(10).AddMilliseconds(1);

        var tMid = t0.AddSeconds(5);
        Assert.True(registry.TryGetOldestPendingLoopbackPanelNavigation(
            IsLoopbackPanel,
            tMid,
            out var youngAge,
            out _));
        Assert.True(
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
                hasPendingLoopbackPanelNavigation: true,
                oldestPendingAge: youngAge,
                pendingDeadline: deadline));

        Assert.True(registry.TryGetOldestPendingLoopbackPanelNavigation(
            IsLoopbackPanel,
            tExpire,
            out var age,
            out _));
        Assert.True(age >= deadline);
        Assert.False(
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
                hasPendingLoopbackPanelNavigation: true,
                oldestPendingAge: age,
                pendingDeadline: deadline));

        Assert.Equal(1, registry.PruneExpiredLoopbackPanelNavigations(IsLoopbackPanel, tExpire, deadline));
        Assert.False(registry.TryGetOldestPendingLoopbackPanelNavigation(
            IsLoopbackPanel,
            tExpire,
            out _,
            out _));
        Assert.False(
            LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
                hasPendingLoopbackPanelNavigation: false,
                oldestPendingAge: TimeSpan.Zero,
                pendingDeadline: deadline));
    }

    [Fact]
    public void completion_removes_entry_without_unbounded_growth()
    {
        var registry = new PanelNavigationPendingRegistry();
        for (ulong id = 1; id <= 20; id++)
        {
            registry.RecordNavigationStarting(id, $"http://127.0.0.1:5000/operator/{id}", DateTime.UtcNow);
        }

        Assert.Equal(20, registry.PendingCount);
        for (ulong id = 1; id <= 20; id++)
        {
            Assert.True(registry.TryTakeNavigationCompleted(id, out _));
        }

        Assert.Equal(0, registry.PendingCount);
        Assert.False(registry.TryTakeNavigationCompleted(5, out _));
    }
}