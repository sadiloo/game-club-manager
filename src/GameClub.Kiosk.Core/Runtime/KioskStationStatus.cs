using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Connection/status line for the lock screen and playing shell (display only).</summary>
public static class KioskStationStatus
{
    /// <summary>Two heartbeat intervals (5 s) plus margin — no ack while transport is up.</summary>
    public const int NoServerResponseAfterSeconds = 12;

    public static string Resolve(
        string stationId,
        KioskTransportPhase transportPhase,
        bool hubTransportConnected,
        bool signalRAutoReconnecting,
        bool pairingJustFailed,
        bool hasOpenSession,
        int consecutiveHeartbeatFailures,
        DateTime? lastHeartbeatAckUtc,
        DateTime utcNow,
        long? estimatedRemainingSeconds,
        string? lastErrorDetail)
    {
        if (pairingJustFailed)
        {
            return $"Station {stationId} — not connected";
        }

        var offline = !hubTransportConnected
            || signalRAutoReconnecting
            || transportPhase is KioskTransportPhase.Disconnected
                or KioskTransportPhase.Reconnecting
                or KioskTransportPhase.Connecting
                or KioskTransportPhase.LastError;

        if (hasOpenSession && !offline && estimatedRemainingSeconds is long seconds)
        {
            var noResponse = consecutiveHeartbeatFailures >= 2
                || (lastHeartbeatAckUtc.HasValue
                    && utcNow - lastHeartbeatAckUtc.Value > TimeSpan.FromSeconds(NoServerResponseAfterSeconds));
            if (hubTransportConnected && noResponse)
            {
                return $"Station {stationId} — no server response";
            }

            return $"Station {stationId} — ~{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} left";
        }

        if (offline)
        {
            if (transportPhase == KioskTransportPhase.LastError && !string.IsNullOrWhiteSpace(lastErrorDetail))
            {
                return $"Station {stationId} — {lastErrorDetail}";
            }

            return $"Station {stationId} — reconnecting…";
        }

        if (!hasOpenSession && hubTransportConnected && transportPhase == KioskTransportPhase.Connected)
        {
            return $"Station {stationId} — connected";
        }

        return transportPhase switch
        {
            KioskTransportPhase.Connecting => $"Station {stationId} — connecting…",
            _ => $"Station {stationId} — connecting…",
        };
    }

    public static bool ContainsConnectedWording(string statusText)
        => statusText.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0
            && statusText.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) < 0
            && statusText.IndexOf("reconnecting", StringComparison.OrdinalIgnoreCase) < 0;
}