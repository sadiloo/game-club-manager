using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class RunningFlapBackoffResetTests
{
    [Fact]
    public void five_minute_running_flap_allows_at_most_one_retry_reset_per_backoff_window()
    {
        var stability = new RunningStabilityTracker();
        var retry = new PanelNavigationRetryPolicy();
        var t0 = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc);
        var resetEvents = 0;
        var visibleChanges = 0;
        var previousFailingVisible = false;
        for (var tick = 0; tick < 60; tick++)
        {
            var utc = t0.AddSeconds(tick * 5);
            var running = tick % 6 < 4;
            stability.Observe(
                running ? ShellServerVisualState.Running : ShellServerVisualState.Stopped,
                utc);
            if (stability.IsRunningStable(utc))
            {
                if (retry.FailureCount > 0)
                {
                    resetEvents++;
                }

                retry.Reset();
            }

            retry.RecordFailure(utc);
            var failingVisible = retry.FailureCount > 0 && !retry.CanAttempt(utc);
            if (failingVisible != previousFailingVisible)
            {
                visibleChanges++;
                previousFailingVisible = failingVisible;
            }
        }

        Assert.InRange(visibleChanges, 0, 12);
        Assert.InRange(resetEvents, 0, 5);
    }
}