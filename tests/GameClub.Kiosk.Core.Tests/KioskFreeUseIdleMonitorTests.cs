using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFreeUseIdleMonitorTests
{
    [Fact]
    public void warning_duration_with_n_1_is_30_seconds()
    {
        Assert.Equal(30, KioskFreeUseIdleMonitor.WarningSecondsForIdleMinutes(1));
        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var mon = new KioskFreeUseIdleMonitor(t0);
        mon.SetIdleMinutes(1);
        var lockAt = t0.AddMinutes(1);
        Assert.True(mon.TryGetWarningCountdown(lockAt.AddSeconds(-20), out var sec));
        Assert.InRange(sec, 1, 30);
    }

    [Fact]
    public void n_reduced_below_idle_still_warns_before_lock()
    {
        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var mon = new KioskFreeUseIdleMonitor(t0);
        mon.SetIdleMinutes(5);
        mon.SetIdleMinutes(2);
        var at = t0.AddMinutes(4);
        Assert.False(mon.ShouldAutoLock(at));
        Assert.True(mon.TryGetWarningCountdown(at, out _));
        Assert.False(mon.ShouldAutoLock(at.AddSeconds(30)));
        Assert.True(mon.ShouldAutoLock(at.AddSeconds(60)));
    }

    [Fact]
    public void input_cancels_warning_and_resets_idle()
    {
        var t0 = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var mon = new KioskFreeUseIdleMonitor(t0);
        mon.SetIdleMinutes(5);
        var warnAt = t0.AddMinutes(4);
        mon.RecordInput(warnAt.AddSeconds(10));
        Assert.False(mon.TryGetWarningCountdown(warnAt.AddSeconds(30), out _));
        Assert.False(mon.ShouldAutoLock(t0.AddMinutes(9)));
        Assert.False(mon.ShouldAutoLock(t0.AddMinutes(10).AddSeconds(10)));
        Assert.True(mon.ShouldAutoLock(t0.AddMinutes(11).AddSeconds(10)));
    }
}