namespace GameClub.Kiosk.Core.Runtime;

public enum KioskStatusRootKind
{
    Unpaired,
    Paired,
    Playing,
    StationWithConnection,
    Locked,
    LockedLeaseExpired,
}

public enum KioskStatusConnectionKind
{
    NotConnected,
    NoResponse,
    Reconnecting,
    Connected,
    Connecting,
    TimeRemaining,
}

/// <summary>Structured lock/playing status line (spec §15.4); UI builds runs from this.</summary>
public readonly record struct KioskStatusLineState(
    KioskStatusRootKind Root,
    string? StationId,
    KioskStatusConnectionKind? Connection,
    TimeSpan? TimeRemaining)
{
    public static KioskStatusLineState Unpaired()
        => new(KioskStatusRootKind.Unpaired, null, null, null);

    public static KioskStatusLineState Paired(string stationId)
        => new(KioskStatusRootKind.Paired, stationId, null, null);

    public static KioskStatusLineState Playing(string stationId)
        => new(KioskStatusRootKind.Playing, stationId, null, null);
}