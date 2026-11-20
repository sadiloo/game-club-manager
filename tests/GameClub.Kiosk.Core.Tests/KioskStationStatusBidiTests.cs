using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskStationStatusBidiTests
{
    [Fact]
    public void lock_status_isolates_station_id_with_fsi_pdi()
    {
        const string station = "PC-9";
        var text = KioskStationStatus.FormatLocalLockScreen(
            station,
            leaseExpired: false,
            KioskTransportPhase.Connected,
            hubTransportConnected: true,
            signalRAutoReconnecting: false,
            pairingJustFailed: false,
            consecutiveHeartbeatFailures: 0,
            lastHeartbeatAckUtc: DateTime.UtcNow,
            utcNow: DateTime.UtcNow,
            lastErrorDetail: null);
        Assert.Contains('\u2068', text);
        Assert.Contains('\u2069', text);
        Assert.Contains($"⁨{station}⁩", text);
    }
}