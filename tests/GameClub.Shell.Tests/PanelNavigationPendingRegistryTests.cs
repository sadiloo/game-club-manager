using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PanelNavigationPendingRegistryTests
{
    [Fact]
    public void orphan_navigation_id_completion_is_ignored_and_does_not_remove_other_entries()
    {
        var registry = new PanelNavigationPendingRegistry();
        registry.RecordNavigationStarting(1, "http://127.0.0.1:5000/operator/");
        registry.RecordNavigationStarting(2, "http://127.0.0.1:5000/operator/other");

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
        registry.RecordNavigationStarting(10, "http://127.0.0.1:5000/operator/index.html");
        registry.RecordNavigationStarting(11, "http://127.0.0.1:5000/operator/second");

        Assert.True(registry.TryTakeNavigationCompleted(10, out var first));
        Assert.True(registry.TryTakeNavigationCompleted(11, out var second));
        Assert.Equal("http://127.0.0.1:5000/operator/index.html", first);
        Assert.Equal("http://127.0.0.1:5000/operator/second", second);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void completion_removes_entry_without_unbounded_growth()
    {
        var registry = new PanelNavigationPendingRegistry();
        for (ulong id = 1; id <= 20; id++)
        {
            registry.RecordNavigationStarting(id, $"http://127.0.0.1:5000/operator/{id}");
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