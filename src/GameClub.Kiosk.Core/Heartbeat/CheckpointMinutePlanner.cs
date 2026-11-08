namespace GameClub.Kiosk.Core.Heartbeat;

/// <summary>
/// Maps 5-second client heartbeats to at-most-one accepted checkpoint per crossed UTC minute.
/// </summary>
public static class CheckpointMinutePlanner
{
    public static IReadOnlyList<DateTime> MinutesToAccept(DateTime lastAcceptedUtc, DateTime serverNowUtc)
    {
        if (serverNowUtc < lastAcceptedUtc.AddMinutes(1))
        {
            return Array.Empty<DateTime>();
        }

        var results = new List<DateTime>();
        var cursor = lastAcceptedUtc;
        while (serverNowUtc >= cursor.AddMinutes(1))
        {
            cursor = cursor.AddMinutes(1);
            if (cursor > serverNowUtc)
            {
                break;
            }

            results.Add(cursor <= serverNowUtc ? cursor : serverNowUtc);
        }

        return results;
    }
}
