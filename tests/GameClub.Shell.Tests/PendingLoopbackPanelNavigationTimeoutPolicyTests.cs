using System;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PendingLoopbackPanelNavigationTimeoutPolicyTests
{
    [Fact]
    public void deadline_is_ten_seconds_after_second_probe_backoff_step() =>
        Assert.Equal(PanelNavigationRetryBackoff.Delay2, PendingLoopbackPanelNavigationTimeoutPolicy.Deadline);
}