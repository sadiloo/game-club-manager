using System;

namespace GameClub.Shell.Shell;

/// <summary>One-shot timer seam for the loopback panel-navigation deadline (production: DispatcherTimer in MainWindow).</summary>
internal interface IPanelNavTimeoutScheduler
{
    /// <summary>Schedules <paramref name="onFire"/> once after <paramref name="dueIn"/>; disposing the handle stops it.</summary>
    IDisposable Schedule(TimeSpan dueIn, Action onFire);
}

/// <summary>
/// Owns the 10 s loopback panel-navigation deadline timer (moved out of MainWindow unchanged, plus the early-tick
/// re-arm). DispatcherTimer and the system clock both have ~15.6 ms resolution, so a tick can land just before the
/// deadline (CI 1131: 9.985 s). Such a tick re-arms for the remaining time; it never drops the deadline.
/// </summary>
internal sealed class LoopbackPanelNavTimeoutTimer
{
    internal delegate bool TryGetOldestPendingLoopbackNavigation(DateTime utcNow, out TimeSpan age, out ulong navigationId);

    private static readonly TimeSpan MinimumRearm = TimeSpan.FromMilliseconds(1);

    private readonly IPanelNavTimeoutScheduler _scheduler;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<bool> _isClosed;
    private readonly Func<bool> _isCycleConsumed;
    private readonly Func<DateTime, bool> _tryConfirmTimedOut;
    private readonly TryGetOldestPendingLoopbackNavigation _tryGetOldestPending;
    private readonly Action<string, string> _log;
    private IDisposable? _armed;

    public LoopbackPanelNavTimeoutTimer(
        IPanelNavTimeoutScheduler scheduler,
        Func<DateTime> utcNow,
        Func<bool> isClosed,
        Func<bool> isCycleConsumed,
        Func<DateTime, bool> tryConfirmTimedOut,
        TryGetOldestPendingLoopbackNavigation tryGetOldestPending,
        Action<string, string> log)
    {
        _scheduler = scheduler;
        _utcNow = utcNow;
        _isClosed = isClosed;
        _isCycleConsumed = isCycleConsumed;
        _tryConfirmTimedOut = tryConfirmTimedOut;
        _tryGetOldestPending = tryGetOldestPending;
        _log = log;
    }

    public bool IsArmed => _armed is not null;

    public void Arm(ulong navigationId) => ArmCore(navigationId, PendingLoopbackPanelNavigationTimeoutPolicy.Deadline, rearm: false);

    public void Cancel(string reason = "cancel")
    {
        if (_armed is null)
        {
            return;
        }

        _armed.Dispose();
        _armed = null;
        _log("panel-nav-timeout-cancel", $"reason={reason}");
    }

    /// <summary>Time left until the deadline for a pending navigation of <paramref name="age"/> (at least 1 ms).</summary>
    public static TimeSpan RemainingUntilDeadline(TimeSpan age, TimeSpan deadline)
    {
        var remaining = deadline - age;
        return remaining < MinimumRearm ? MinimumRearm : remaining;
    }

    private void ArmCore(ulong navigationId, TimeSpan dueIn, bool rearm)
    {
        Cancel("rearm");
        _armed = _scheduler.Schedule(dueIn, OnTick);
        var deadlineSec = PendingLoopbackPanelNavigationTimeoutPolicy.Deadline.TotalSeconds;
        _log(
            "panel-nav-timeout-arm",
            rearm
                ? $"id={navigationId} deadlineSec={deadlineSec:F0} rearmMs={dueIn.TotalMilliseconds:F0}"
                : $"id={navigationId} deadlineSec={deadlineSec:F0}");
    }

    private void OnTick()
    {
        Cancel("fired");
        if (_isClosed())
        {
            _log("panel-nav-timeout-skip", "reason=closed");
            return;
        }

        if (_isCycleConsumed())
        {
            _log("panel-nav-timeout-skip", "reason=cycleConsumed");
            return;
        }

        var utcNow = _utcNow();
        if (_tryConfirmTimedOut(utcNow))
        {
            return;
        }

        if (!_tryGetOldestPending(utcNow, out var age, out var navigationId))
        {
            _log("panel-nav-timeout-skip", "reason=no-pending");
            return;
        }

        var remaining = RemainingUntilDeadline(age, PendingLoopbackPanelNavigationTimeoutPolicy.Deadline);
        _log(
            "panel-nav-timeout-skip",
            $"reason=not-due pendingId={navigationId} ageSec={age.TotalSeconds:F3} rearmMs={remaining.TotalMilliseconds:F0}");
        ArmCore(navigationId, remaining, rearm: true);
    }
}
