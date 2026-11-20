using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class RunningStabilityTrackerTests
{
    [Fact]
    public void panel_retry_reset_requires_sixty_seconds_stable_running()
    {
        var tracker = new RunningStabilityTracker();
        var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        tracker.Observe(ShellServerVisualState.Running, t0);
        Assert.False(tracker.IsRunningStable(t0.AddSeconds(59)));
        Assert.True(tracker.IsRunningStable(t0.AddSeconds(60)));
    }

    [Fact]
    public void five_minute_running_flap_resets_stability_before_sixty_seconds()
    {
        var tracker = new RunningStabilityTracker();
        var t0 = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        for (var minute = 0; minute < 5; minute++)
        {
            var sliceStart = t0.AddMinutes(minute);
            tracker.Observe(ShellServerVisualState.Running, sliceStart);
            tracker.Observe(ShellServerVisualState.Stopped, sliceStart.AddSeconds(10));
            tracker.Observe(ShellServerVisualState.Running, sliceStart.AddSeconds(20));
            Assert.False(tracker.IsRunningStable(sliceStart.AddSeconds(55)));
        }
    }
}