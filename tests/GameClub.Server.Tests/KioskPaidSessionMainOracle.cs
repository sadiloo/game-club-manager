namespace GameClub.Server.Tests;

/// <summary>
/// Bill fingerprint for the paid kiosk parity scenario (no HTTP LastSeen stamps), re-captured on main
/// <c>1f0c4e718d205906eb9cae28947802d70d04fe5b</c> (after PR #40 PC-internet: segment Mode Online + display line)
/// by running the identical scenario on a pure-main worktree; byte-identical to the merged head's withoutHttp bill.
/// Originally captured on 977703de via <c>scripts/agent/capture-kiosk-parity-oracle.sh</c>; amounts unchanged.
/// </summary>
internal static class KioskPaidSessionMainOracle
{
    public const string WithoutHttpBillFingerprint =
        """{"UnroundedPayableToman":60000,"RoundingToman":0,"CollectibleToman":60000,"TimeGrossToman":60000,"BuffetGrossToman":0,"ServicesGrossToman":0,"LevelDiscountToman":0,"BirthdayDiscountToman":0,"WriteOffToman":0,"FrozenAtUtc":"2026-06-10T11:15:00Z","StopReason":"logout","Segments":[{"SegmentKey":"a","PlayerCount":1,"Mode":"Online","HourlyToman":80000,"StartUtc":"2026-06-10T10:30:00Z","EndUtc":"2026-06-10T11:15:00Z","DisplayLine":"\u0627\u06CC\u0646\u062A\u0631\u0646\u062A \u0648\u0635\u0644 \u00B7 \u06F8\u06F0\u066C\u06F0\u06F0\u06F0 \u062A\u0648\u0645\u0627\u0646/\u0633\u0627\u0639\u062A (\u0622\u0646\u0644\u0627\u06CC\u0646) \u00B7 \u06F1\u06F4:\u06F0\u06F0\u2013\u06F1\u06F4:\u06F4\u06F5 \u00B7 \u06F4\u06F5 \u062F\u0642\u06CC\u0642\u0647 \u00B7 \u06F6\u06F0\u066C\u06F0\u06F0\u06F0"}],"SaleLines":[]}""";
}