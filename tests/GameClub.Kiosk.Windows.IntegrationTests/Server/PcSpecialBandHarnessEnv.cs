#if NET10_0_OR_GREATER
using GameClub.Billing;
using GameClub.Server.Testing;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal static class PcSpecialBandHarnessEnv
{
    public static string DayBandHostArgs() => "--pc-band day";

    public static string NightBandHostArgs() => "--pc-band night";

    public static string HostArgsFromBand((TimeOfDay Start, TimeOfDay End) band)
    {
        var options = PcSpecialBandHarnessSchedule.ToOptions(band);
        return HostArgsFromOptions(options);
    }

    public static string HostArgsFromOptions(PcSpecialBandHarnessOptions options)
        => $"--pc-normal-start {options.PcNormalStart} --pc-normal-end {options.PcNormalEnd} "
           + $"--pc-vip-start {options.PcVipStart} --pc-vip-end {options.PcVipEnd}";
}
#endif