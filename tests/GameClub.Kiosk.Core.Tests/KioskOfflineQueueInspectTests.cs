using GameClub.Kiosk.Core.Admin;
using GameClub.Kiosk.Core.Offline;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskOfflineQueueInspectTests
{
    [Fact]
    public async Task queued_end_beats_restart_inspect()
    {
        var path = Path.Combine(Path.GetTempPath(), "gcm-events-" + Guid.NewGuid().ToString("N") + ".db");
        var freeUseId = Guid.NewGuid();
        var queue = new SqliteOfflineEventQueue(path);
        await queue.EnqueueAsync(
            Guid.NewGuid(),
            "kiosk_free_use_end",
            $$"""{"freeUseId":"{{freeUseId}}","reason":"manual","endedAtUtc":"2026-06-01T12:00:00.0000000Z"}""",
            DateTime.UtcNow);
        Assert.True(KioskOfflineQueueInspect.HasPendingFreeUseEnd(path, freeUseId));
        Assert.Null(KioskFreeUseBootPolicy.RestartEndUtcIfNeeded(
            new KioskFreeUseLocalState { OpenFreeUseId = freeUseId, LastAliveUtc = DateTime.UtcNow },
            hasQueuedRealEnd: true));
        queue.Close();
        File.Delete(path);
    }
}