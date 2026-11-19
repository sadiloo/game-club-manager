#if NET10_0_OR_GREATER
namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class PcSpecialBandHarnessEnv
{
    public static string DayBandHostArgs() => "--pc-band day";

    public static string NightBandHostArgs() => "--pc-band night";
}
#endif