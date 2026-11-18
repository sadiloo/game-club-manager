using System;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetDriftWatchdog
{
    internal const int ProductionDriftIntervalMs = 60_000;

    internal static int ResolveIntervalMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_DRIFT_INTERVAL_MS");
        if (int.TryParse(raw, out var ms) && ms > 0)
        {
            return ms;
        }

        return ProductionDriftIntervalMs;
    }
}