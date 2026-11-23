using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class HealthyRunningPanelProbePolicyTests
{
    [Fact]
    public void dispatcher_poll_interval_is_one_second() =>
        Assert.Equal(TimeSpan.FromSeconds(1), HealthyRunningPanelProbePolicy.DispatcherPollInterval);

    [Fact]
    public void per_probe_timeout_is_at_most_one_second() =>
        Assert.Equal(TimeSpan.FromSeconds(3), HealthyRunningPanelProbePolicy.PerProbeTimeout);

    [Fact]
    public void confirmed_failure_requires_two_consecutive_probe_failures() =>
        Assert.Equal(2, HealthyRunningPanelProbePolicy.ConsecutiveFailuresForConfirmation);

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    public void should_probe_on_poller_tick_matches_running_panel_and_origin_state(
        bool runningPanelLoadFailing,
        bool panelNavigated,
        bool activeOnPanelOrigin,
        bool expected)
    {
        Assert.Equal(
            expected,
            HealthyRunningPanelProbePolicy.ShouldProbeOnPollerTick(
                runningPanelLoadFailing,
                panelNavigated,
                activeOnPanelOrigin));
    }
}