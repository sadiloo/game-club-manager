#nullable disable
namespace GameClub.Kiosk.Windows.IntegrationTests;

/// <summary>
/// The "within one tick" tolerance of the live WPF remaining-time tests (R10m, QA M7). Both WPF tests take their
/// bounds from here so the exact ±1 s pin is unit-tested on ubuntu (SeededPcBandTariffTests) and a wider tolerance
/// fails there. The server's display-build instant is not exposed to the test without a src change.
/// </summary>
internal static class RemainingTimeOneTick
{
    public const int ToleranceSeconds = 1;

    public static int Low(int expectedSeconds) => expectedSeconds - ToleranceSeconds;

    public static int High(int expectedSeconds) => expectedSeconds + ToleranceSeconds;

    public static bool Within(int expectedSeconds, int displayedSeconds) =>
        displayedSeconds >= Low(expectedSeconds) && displayedSeconds <= High(expectedSeconds);
}
