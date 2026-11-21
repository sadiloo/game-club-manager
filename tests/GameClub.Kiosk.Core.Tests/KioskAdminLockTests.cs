using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskAdminLockTests
{
    [Fact]
    public void operator_one_wrong_then_right_resets_count()
    {
        var utc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var gate = new KioskAdminLock();
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        Assert.False(gate.IsLocked(KioskAdminRole.Operator, utc, out _));
        gate.RegisterSuccess(KioskAdminRole.Operator);
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        Assert.False(gate.IsLocked(KioskAdminRole.Operator, utc, out _));
    }

    [Fact]
    public void operator_two_wrong_locks_and_right_password_still_locked()
    {
        var utc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var gate = new KioskAdminLock();
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        Assert.True(gate.IsLocked(KioskAdminRole.Operator, utc, out var until));
        Assert.NotNull(until);
        Assert.True(gate.IsLocked(KioskAdminRole.Operator, utc.AddMinutes(5), out _));
    }

    [Fact]
    public void manager_lock_has_no_early_unlock_only_after_fifteen_minutes()
    {
        var utc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var gate = new KioskAdminLock();
        gate.RegisterWrong(KioskAdminRole.Manager, utc);
        gate.RegisterWrong(KioskAdminRole.Manager, utc);
        Assert.True(gate.IsLocked(KioskAdminRole.Manager, utc.AddMinutes(14), out _));
        Assert.False(gate.IsLocked(KioskAdminRole.Manager, utc.AddMinutes(15), out _));
    }

    [Fact]
    public void manager_clearing_operator_lock_via_helper()
    {
        var utc = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        var gate = new KioskAdminLock();
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        gate.RegisterWrong(KioskAdminRole.Operator, utc);
        gate.ClearOperatorLock();
        Assert.False(gate.IsLocked(KioskAdminRole.Operator, utc, out _));
    }
}