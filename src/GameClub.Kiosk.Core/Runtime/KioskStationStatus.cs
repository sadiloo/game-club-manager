using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Runtime;

/// <summary>Connection/status line for the lock screen and playing shell (display only).</summary>
public static class KioskStationStatus
{
    /// <summary>Two heartbeat intervals (5 s) plus margin — no ack while transport is up.</summary>
    public const int NoServerResponseAfterSeconds = 12;

    public static KioskStatusLineState ResolveState(
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
            return new KioskStatusLineState(
                KioskStatusRootKind.StationWithConnection,
                stationId,
                KioskStatusConnectionKind.NotConnected,
                null);
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
                    return StationConn(stationId, KioskStatusConnectionKind.NoResponse);
                }

                return new KioskStatusLineState(
                    KioskStatusRootKind.StationWithConnection,
                    stationId,
                    KioskStatusConnectionKind.TimeRemaining,
                    TimeSpan.FromSeconds(seconds));
            }

            if (hubTransportConnected && noResponse)
            {
                return StationConn(stationId, KioskStatusConnectionKind.NoResponse);
            }
        }

        if (offline)
        {
            if (transportPhase == KioskTransportPhase.LastError
                && !string.IsNullOrWhiteSpace(lastErrorDetail))
            {
                return StationConn(stationId, KioskStatusConnectionKind.NotConnected);
            }

            return StationConn(stationId, KioskStatusConnectionKind.Reconnecting);
        }

        if (!hasOpenSession && hubTransportConnected && transportPhase == KioskTransportPhase.Connected)
        {
            var noResponse = consecutiveHeartbeatFailures >= 2
                || (lastHeartbeatAckUtc.HasValue
                    && utcNow - lastHeartbeatAckUtc.Value > TimeSpan.FromSeconds(NoServerResponseAfterSeconds));
            if (noResponse)
            {
                return StationConn(stationId, KioskStatusConnectionKind.NoResponse);
            }

            return StationConn(stationId, KioskStatusConnectionKind.Connected);
        }

        return StationConn(stationId, KioskStatusConnectionKind.Connecting);
    }

    public static KioskStatusLineState FormatLocalLockScreenState(
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
        var inner = ResolveState(
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
        var root = leaseExpired ? KioskStatusRootKind.LockedLeaseExpired : KioskStatusRootKind.Locked;
        return inner with { Root = root };
    }

    public static bool ImpliesBareConnected(KioskStatusLineState state)
        => KioskStatusMessages.IsBareConnectedConnection(state.Connection);

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
        => KioskStatusPresentationBuilder.Build(
            ResolveState(
                stationId,
                transportPhase,
                hubTransportConnected,
                signalRAutoReconnecting,
                pairingJustFailed,
                hasOpenSession,
                consecutiveHeartbeatFailures,
                lastHeartbeatAckUtc,
                utcNow,
                estimatedRemainingSeconds,
                lastErrorDetail),
            loginErrorText: null,
            transientErrorKey: null).ToCombinedText();

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
        => KioskStatusPresentationBuilder.Build(
            FormatLocalLockScreenState(
                stationId,
                leaseExpired,
                transportPhase,
                hubTransportConnected,
                signalRAutoReconnecting,
                pairingJustFailed,
                consecutiveHeartbeatFailures,
                lastHeartbeatAckUtc,
                utcNow,
                lastErrorDetail),
            loginErrorText: null,
            transientErrorKey: null).ToCombinedText();

    private static KioskStatusLineState StationConn(string stationId, KioskStatusConnectionKind conn)
        => new(KioskStatusRootKind.StationWithConnection, stationId, conn, null);
}