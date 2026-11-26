using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class RunningPanelLoadFailingPollerPolicyTests
{
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    public void applies_only_when_panel_failing_and_offline_not_shown(
        bool runningPanelLoadFailing,
        bool offlineShown,
        bool expected)
    {
        Assert.Equal(expected, RunningPanelLoadFailingPollerPolicy.Applies(runningPanelLoadFailing, offlineShown));
    }

    [Fact]
    public void skips_offline_represent_when_present_in_flight()
    {
        Assert.Equal(
            PanelFailingHiddenOfflinePresentAction.SkipPresentOfflineInFlight,
            RunningPanelLoadFailingPollerPolicy.ResolvePresentAction("panel-unavailable"));
    }

    [Fact]
    public void re_presents_panel_unavailable_when_no_present_in_flight()
    {
        Assert.Equal(
            PanelFailingHiddenOfflinePresentAction.PresentPanelUnavailable,
            RunningPanelLoadFailingPollerPolicy.ResolvePresentAction(null));
    }
}