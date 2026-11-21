using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class HealthyRunningPanelProbePolicyTests
{
    [Fact]
    public void dispatcher_poll_interval_is_shorter_than_service_poller_tick() =>
        Assert.True(HealthyRunningPanelProbePolicy.DispatcherPollInterval < TimeSpan.FromSeconds(5));

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