using GameClub.Kiosk.Core.Admin;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskFreeUseBootPolicyTests
{
    [Fact]
    public void queued_end_beats_restart()
    {
        var state = new KioskFreeUseLocalState
        {
            OpenFreeUseId = Guid.NewGuid(),
            LastAliveUtc = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
        };
        Assert.Null(KioskFreeUseBootPolicy.RestartEndUtcIfNeeded(state, hasQueuedRealEnd: true));
    }

    [Fact]
    public void restart_end_equals_last_alive()
    {
        var alive = new DateTime(2026, 6, 1, 12, 30, 0, DateTimeKind.Utc);
        var state = new KioskFreeUseLocalState
        {
            OpenFreeUseId = Guid.NewGuid(),
            LastAliveUtc = alive,
        };
        Assert.Equal(alive, KioskFreeUseBootPolicy.RestartEndUtcIfNeeded(state, hasQueuedRealEnd: false));
    }
}