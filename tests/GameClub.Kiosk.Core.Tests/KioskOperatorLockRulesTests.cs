using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskOperatorLockRulesTests
{
    [Fact]
    public void session_lock_requires_transport()
    {
        Assert.False(KioskOperatorLockRules.CanLockOpenSessionOnline(transportConnected: false));
        Assert.True(KioskOperatorLockRules.CanLockOpenSessionOnline(transportConnected: true));
    }

    [Fact]
    public void free_use_start_requires_transport_but_end_can_queue_offline()
    {
        Assert.Equal("kiosk.freeuse.err.offline", KioskOperatorLockRules.OfflineFreeUseStartErrorKey);
        Assert.False(KioskOperatorLockRules.CanStartFreeUseOnline(transportConnected: false));
        Assert.True(KioskOperatorLockRules.CanStartFreeUseOnline(transportConnected: true));
        Assert.True(KioskOperatorLockRules.CanEndFreeUseWhileOffline);
    }
}