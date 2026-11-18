namespace GameClub.Kiosk.Core.Runtime;

public enum KioskTransportPhase
{
    Unconfigured,
    Connecting,
    Connected,
    Disconnected,
    Reconnecting,
    LastError,
}