using System;
using System.Collections.Generic;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

public sealed class PendingLoopbackPanelNavigationExpiryPolicyTests
{
  private static bool IsLoopbackPanel(string uri) =>
    uri.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase);

  [Theory]
  [InlineData(10, true)]
  [InlineData(9.9, false)]
  public void has_reached_deadline_at_second_boundary(double ageSeconds, bool expectedExpires)
  {
    var deadline = PendingLoopbackPanelNavigationTimeoutPolicy.Deadline;
    Assert.Equal(
      expectedExpires,
      PendingLoopbackPanelNavigationExpiryPolicy.HasReachedDeadline(
        TimeSpan.FromSeconds(ageSeconds),
        deadline));
  }

  [Fact]
  public void has_reached_deadline_one_millisecond_past_deadline_expires()
  {
    var deadline = PendingLoopbackPanelNavigationTimeoutPolicy.Deadline;
    Assert.True(
      PendingLoopbackPanelNavigationExpiryPolicy.HasReachedDeadline(
        deadline + TimeSpan.FromMilliseconds(1),
        deadline));
  }

  [Fact]
  public void missing_completion_at_deadline_confirms_timeout_once_and_allows_no_defer()
  {
    var registry = new PanelNavigationPendingRegistry();
    var timedOutIds = new HashSet<ulong>();
    var failureCount = 0;
    var t0 = new DateTime(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
    var deadline = PendingLoopbackPanelNavigationTimeoutPolicy.Deadline;
    const ulong navId = 42;
    registry.RecordNavigationStarting(navId, "http://127.0.0.1:5000/operator/", t0);
    var tExpire = t0 + deadline + TimeSpan.FromMilliseconds(1);

    Assert.True(registry.TryGetOldestPendingLoopbackPanelNavigation(
      IsLoopbackPanel,
      tExpire,
      out var age,
      out var id));
    Assert.Equal(navId, id);
    Assert.True(PendingLoopbackPanelNavigationExpiryPolicy.HasReachedDeadline(age, deadline));
    Assert.True(timedOutIds.Add(navId));
    failureCount++;
    registry.TryRemovePendingLoopbackNavigation(navId);
    Assert.False(
      LoopbackPanelNavigationInFlightPolicy.ShouldDeferNewPanelNavigation(
        hasPendingLoopbackPanelNavigation: false,
        oldestPendingAge: TimeSpan.Zero,
        pendingDeadline: deadline));
    Assert.False(registry.TryGetOldestPendingLoopbackPanelNavigation(
      IsLoopbackPanel,
      tExpire,
      out _,
      out _));

    Assert.False(timedOutIds.Add(navId));
    Assert.Equal(1, failureCount);
  }
}