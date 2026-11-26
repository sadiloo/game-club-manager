using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskStationStatusBidiTests
{
    [Fact]
    public void lock_status_isolates_station_id_in_ltr_segment()
    {
        const string station = "PC-9";
        var state = KioskStationStatus.FormatLocalLockScreenState(
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
        var presentation = KioskStatusPresentationBuilder.Build(state, null, null);
        Assert.Contains(presentation.StatusSegments, s => s.IsolateLatin && s.Text == station);
        Assert.False(presentation.ToCombinedText().Contains('\u2068'));
    }
}