using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Connection/status line for the lock screen and playing shell (display only).</summary>
public static class KioskStationStatus
{
    /// <summary>Two heartbeat intervals (5 s) plus margin — no ack while transport is up.</summary>
    public const int NoServerResponseAfterSeconds = 12;

    private const char Fsi = '\u2068';
    private const char Pdi = '\u2069';

    public static string IsolateStationId(string stationId) => $"{Fsi}{stationId}{Pdi}";

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
        var pc = IsolateStationId(stationId);
        if (pairingJustFailed)
        {
            return KioskStatusMessages.FormatStationLine(KioskStatusMessages.Keys.StationLineNotConnected, pc);
        }

        var offline = !hubTransportConnected
            || signalRAutoReconnecting
            || transportPhase is KioskTransportPhase.Disconnected
                or KioskTransportPhase.Reconnecting
                or KioskTransportPhase.Connecting
                or KioskTransportPhase.LastError;

        if (hasOpenSession && !offline)
        {
            var noResponse = consecutiveHeartbeatFailures >= 2
                || (lastHeartbeatAckUtc.HasValue
                    && utcNow - lastHeartbeatAckUtc.Value > TimeSpan.FromSeconds(NoServerResponseAfterSeconds));
            if (estimatedRemainingSeconds is long seconds)
            {
                if (hubTransportConnected && noResponse)
                {
                    return KioskStatusMessages.FormatStationLine(
                        KioskStatusMessages.Keys.StationLineNoServerResponse,
                        pc);
                }

                return KioskStatusMessages.FormatTimeRemaining(pc, TimeSpan.FromSeconds(seconds));
            }

            if (hubTransportConnected && noResponse)
            {
                return KioskStatusMessages.FormatStationLine(
                    KioskStatusMessages.Keys.StationLineNoServerResponse,
                    pc);
            }
        }

        if (offline)
        {
            if (transportPhase == KioskTransportPhase.LastError && !string.IsNullOrWhiteSpace(lastErrorDetail))
            {
                return KioskStatusMessages.FormatStationTransportError(pc, lastErrorDetail!);
            }

            return KioskStatusMessages.FormatStationLine(KioskStatusMessages.Keys.StationLineReconnecting, pc);
        }

        if (!hasOpenSession && hubTransportConnected && transportPhase == KioskTransportPhase.Connected)
        {
            var noResponse = consecutiveHeartbeatFailures >= 2
                || (lastHeartbeatAckUtc.HasValue
                    && utcNow - lastHeartbeatAckUtc.Value > TimeSpan.FromSeconds(NoServerResponseAfterSeconds));
            if (noResponse)
            {
                return KioskStatusMessages.FormatStationLine(
                    KioskStatusMessages.Keys.StationLineNoServerResponse,
                    pc);
            }

            return KioskStatusMessages.FormatStationLine(KioskStatusMessages.Keys.StationLineConnected, pc);
        }

        return KioskStatusMessages.FormatStationLine(KioskStatusMessages.Keys.StationLineConnecting, pc);
    }

    public static bool ContainsConnectedWording(string statusText)
        => statusText.IndexOf("connected", StringComparison.OrdinalIgnoreCase) >= 0
            && statusText.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) < 0
            && statusText.IndexOf("reconnecting", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>Main lock prefix plus O4-d connection suffix (never bare session countdown while locked).</summary>
    public static string FormatLocalLockScreen(
        string stationId,
        bool leaseExpired,
        KioskTransportPhase transportPhase,
        bool hubTransportConnected,
        bool signalRAutoReconnecting,
        bool pairingJustFailed,
        int consecutiveHeartbeatFailures,
        DateTime? lastHeartbeatAckUtc,
        DateTime utcNow,
        string? lastErrorDetail)
    {
        var connection = Resolve(
            stationId,
            transportPhase,
            hubTransportConnected,
            signalRAutoReconnecting,
            pairingJustFailed,
            hasOpenSession: false,
            consecutiveHeartbeatFailures,
            lastHeartbeatAckUtc,
            utcNow,
            estimatedRemainingSeconds: null,
            lastErrorDetail);
        var pc = IsolateStationId(stationId);
        var prefix = KioskStatusMessages.StationLinePrefixFor(pc);
        var suffix = connection.StartsWith(prefix, StringComparison.Ordinal)
            ? connection.Substring(prefix.Length)
            : connection;
        var lockKey = leaseExpired
            ? KioskStatusMessages.Keys.LockedLeaseExpired
            : KioskStatusMessages.Keys.LockedActive;
        return KioskStatusMessages.FormatLockedLine(lockKey, pc, suffix);
    }
}