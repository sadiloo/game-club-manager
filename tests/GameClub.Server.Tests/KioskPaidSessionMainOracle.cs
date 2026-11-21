namespace GameClub.Server.Tests;

/// <summary>
/// Bill fingerprint for the paid kiosk parity scenario (no HTTP LastSeen stamps), captured on main
/// <c>977703de90e5aa4deb99ed1ff8bae7f0fdddc095</c> via <c>scripts/agent/capture-kiosk-parity-oracle.sh</c>
/// (verified equal to head-withoutHttp while Billing and session-stop paths match main on that SHA).
/// </summary>
internal static class KioskPaidSessionMainOracle
{
    public const string WithoutHttpBillFingerprint =
        """{"UnroundedPayableToman":60000,"RoundingToman":0,"CollectibleToman":60000,"TimeGrossToman":60000,"BuffetGrossToman":0,"ServicesGrossToman":0,"LevelDiscountToman":0,"BirthdayDiscountToman":0,"WriteOffToman":0,"FrozenAtUtc":"2026-06-10T11:15:00Z","StopReason":"logout","Segments":[{"SegmentKey":"a","PlayerCount":1,"Mode":"Offline","HourlyToman":80000,"StartUtc":"2026-06-10T10:30:00Z","EndUtc":"2026-06-10T11:15:00Z","DisplayLine":null}],"SaleLines":[]}""";
}