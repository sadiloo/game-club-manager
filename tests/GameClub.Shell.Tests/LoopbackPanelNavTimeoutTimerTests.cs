using System;
using System.Collections.Generic;
using System.Linq;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Tests;

/// <summary>
/// Pins for the loopback panel-nav deadline timer (CI 1131: DispatcherTimer ticked at 9.985 s, the tick found the
/// navigation "not due" and the deadline was dropped). Virtual clock + manual one-shot scheduler; no real time.
/// The confirm step mirrors MainWindow.TryConfirmTimedOutLoopbackPanelNavigation with the real registry and policy.
/// </summary>
public sealed class LoopbackPanelNavTimeoutTimerTests
{
    private static readonly DateTime T0 = new(2026, 11, 26, 5, 57, 51, DateTimeKind.Utc);
    private const ulong NavId = 2;
    private const string PanelUri = "http://127.0.0.1:5000/operator/";

    private sealed class ManualScheduler : IPanelNavTimeoutScheduler
    {
        public sealed class Entry : IDisposable
        {
            public required DateTime ScheduledAt { get; init; }
            public required TimeSpan DueIn { get; init; }
            public required Action OnFire { get; init; }
            public bool Disposed { get; private set; }
            public DateTime DueAt => ScheduledAt + DueIn;
            public void Dispose() => Disposed = true;
        }

        private readonly Func<DateTime> _now;

        public ManualScheduler(Func<DateTime> now) => _now = now;

        public List<Entry> Entries { get; } = new();

        public IEnumerable<Entry> Live => Entries.Where(e => !e.Disposed);

        public IDisposable Schedule(TimeSpan dueIn, Action onFire)
        {
            var entry = new Entry { ScheduledAt = _now(), DueIn = dueIn, OnFire = onFire };
            Entries.Add(entry);
            return entry;
        }
    }

    private sealed class Harness
    {
        public DateTime Now = T0;
        public readonly PanelNavigationPendingRegistry Registry = new();
        public readonly HashSet<ulong> TimedOutIds = new();
        public readonly List<string> Log = new();
        public readonly ManualScheduler Scheduler;
        public readonly LoopbackPanelNavTimeoutTimer Timer;
        public bool Closed;
        public bool CycleConsumed;
        public DateTime? OfflineShownAt;

        public Harness()
        {
            Scheduler = new ManualScheduler(() => Now);
            Timer = new LoopbackPanelNavTimeoutTimer(
                Scheduler,
                () => Now,
                () => Closed,
                () => CycleConsumed,
                TryConfirmTimedOut,
                (DateTime utcNow, out TimeSpan age, out ulong id) =>
                    Registry.TryGetOldestPendingLoopbackPanelNavigation(IsLoopback, utcNow, out age, out id),
                (branch, reason) => Log.Add($"branch={branch} reason={reason}"));
        }

        private static bool IsLoopback(string uri) => uri.Contains("127.0.0.1", StringComparison.Ordinal);

        // Same decision as MainWindow.TryConfirmTimedOutLoopbackPanelNavigation (fromTimer: true).
        private bool TryConfirmTimedOut(DateTime utcNow)
        {
            var deadline = PendingLoopbackPanelNavigationTimeoutPolicy.Deadline;
            if (!Registry.TryGetOldestPendingLoopbackPanelNavigation(IsLoopback, utcNow, out var age, out var id)
                || !PendingLoopbackPanelNavigationExpiryPolicy.HasReachedDeadline(age, deadline)
                || !TimedOutIds.Add(id))
            {
                return false;
            }

            Registry.TryRemovePendingLoopbackNavigation(id);
            CycleConsumed = true;
            OfflineShownAt = utcNow; // HandleRunningPanelNavigationFailure -> panel-unavailable offline page
            return true;
        }

        public void StartNavigation()
        {
            Registry.RecordNavigationStarting(NavId, PanelUri, Now);
            CycleConsumed = false;
            Timer.Arm(NavId);
        }

        /// <summary>Fires the single live timer at <paramref name="at"/> (default: exactly its due time).</summary>
        public ManualScheduler.Entry Fire(DateTime? at = null)
        {
            var live = Scheduler.Live.ToList();
            Assert.Single(live);
            var entry = live[0];
            Now = at ?? entry.DueAt;
            entry.OnFire();
            return entry;
        }
    }

    [Fact]
    public void early_tick_at_9_985s_rearms_for_remaining_time_and_shows_offline_at_10s()
    {
        var h = new Harness();
        h.StartNavigation();
        var first = Assert.Single(h.Scheduler.Live);
        Assert.Equal(PendingLoopbackPanelNavigationTimeoutPolicy.Deadline, first.DueIn);

        h.Fire(T0 + TimeSpan.FromMilliseconds(9_985)); // DispatcherTimer 15 ms early (CI 1131)

        Assert.Null(h.OfflineShownAt);
        var rearmed = Assert.Single(h.Scheduler.Live);
        Assert.NotSame(first, rearmed);
        Assert.Equal(TimeSpan.FromMilliseconds(15), rearmed.DueIn);
        Assert.Contains(h.Log, l => l.Contains("reason=not-due pendingId=2 ageSec=9.985 rearmMs=15", StringComparison.Ordinal));
        Assert.Contains(h.Log, l => l.Contains("branch=panel-nav-timeout-arm reason=id=2 deadlineSec=10 rearmMs=15", StringComparison.Ordinal));

        h.Fire();

        Assert.Equal(T0 + PendingLoopbackPanelNavigationTimeoutPolicy.Deadline, h.OfflineShownAt);
        Assert.Contains(NavId, h.TimedOutIds);
        Assert.Empty(h.Scheduler.Live);
    }

    [Fact]
    public void on_time_tick_at_10s_shows_offline_without_rearm()
    {
        var h = new Harness();
        h.StartNavigation();

        h.Fire();

        Assert.Equal(T0 + PendingLoopbackPanelNavigationTimeoutPolicy.Deadline, h.OfflineShownAt);
        Assert.Single(h.Scheduler.Entries);
        Assert.Empty(h.Scheduler.Live);
        Assert.DoesNotContain(h.Log, l => l.Contains("not-due", StringComparison.Ordinal));
    }

    [Fact]
    public void repeated_early_ticks_keep_rearming_until_the_deadline_is_enforced()
    {
        var h = new Harness();
        h.StartNavigation();

        h.Fire(T0 + TimeSpan.FromMilliseconds(9_985));
        h.Fire(T0 + TimeSpan.FromMilliseconds(9_999)); // re-armed tick also early
        Assert.Null(h.OfflineShownAt);
        Assert.Equal(TimeSpan.FromMilliseconds(1), Assert.Single(h.Scheduler.Live).DueIn);

        h.Fire();

        Assert.Equal(T0 + PendingLoopbackPanelNavigationTimeoutPolicy.Deadline, h.OfflineShownAt);
        Assert.Empty(h.Scheduler.Live);
    }

    [Fact]
    public void tick_after_navigation_completed_does_not_rearm()
    {
        var h = new Harness();
        h.StartNavigation();
        Assert.True(h.Registry.TryTakeNavigationCompleted(NavId, out _));

        h.Fire(T0 + TimeSpan.FromMilliseconds(9_985));

        Assert.Null(h.OfflineShownAt);
        Assert.Empty(h.Scheduler.Live);
        Assert.Contains(h.Log, l => l.Contains("reason=no-pending", StringComparison.Ordinal));
    }

    [Fact]
    public void tick_after_close_or_consumed_cycle_does_not_rearm()
    {
        var closed = new Harness();
        closed.StartNavigation();
        closed.Closed = true;
        closed.Fire(T0 + TimeSpan.FromMilliseconds(9_985));
        Assert.Empty(closed.Scheduler.Live);
        Assert.Contains(closed.Log, l => l.Contains("reason=closed", StringComparison.Ordinal));

        var consumed = new Harness();
        consumed.StartNavigation();
        consumed.CycleConsumed = true;
        consumed.Fire(T0 + TimeSpan.FromMilliseconds(9_985));
        Assert.Empty(consumed.Scheduler.Live);
        Assert.Contains(consumed.Log, l => l.Contains("reason=cycleConsumed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(9_985, 15)]
    [InlineData(10_000, 1)]
    [InlineData(12_000, 1)]
    [InlineData(0, 10_000)]
    public void remaining_until_deadline_is_never_below_one_millisecond(int ageMs, int expectedMs)
        => Assert.Equal(
            TimeSpan.FromMilliseconds(expectedMs),
            LoopbackPanelNavTimeoutTimer.RemainingUntilDeadline(
                TimeSpan.FromMilliseconds(ageMs),
                PendingLoopbackPanelNavigationTimeoutPolicy.Deadline));
}
