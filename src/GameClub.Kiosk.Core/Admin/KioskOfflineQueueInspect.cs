using System.Text.Json;
using GameClub.Kiosk.Core.Offline;

namespace GameClub.Kiosk.Core.Admin;

public static class KioskOfflineQueueInspect
{
    public static bool HasPendingFreeUseEnd(string queueDbPath, Guid freeUseId)
    {
        if (!File.Exists(queueDbPath))
        {
            return false;
        }

        var queue = new SqliteOfflineEventQueue(queueDbPath);
        var pending = queue.PendingAsync().GetAwaiter().GetResult();
        foreach (var ev in pending)
        {
            if (!string.Equals(ev.Type, "kiosk_free_use_end", StringComparison.Ordinal))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(ev.PayloadJson);
            if (doc.RootElement.GetProperty("freeUseId").GetGuid() == freeUseId)
            {
                return true;
            }
        }

        return false;
    }
}