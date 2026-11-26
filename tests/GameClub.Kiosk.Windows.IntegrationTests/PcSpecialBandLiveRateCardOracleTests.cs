#if NET10_0_OR_GREATER
using GameClub.Server.Testing;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests;

public sealed class PcSpecialBandLiveRateCardOracleTests
{
    public static TheoryData<string> HostArgCases { get; } = new()
    {
        "--pc-band night",
        "--pc-band day",
        "--pc-band production",
        "--pc-normal-start 20:13 --pc-normal-end 20:43 --pc-vip-start 20:13 --pc-vip-end 20:43",
    };

    [Theory]
    [MemberData(nameof(HostArgCases))]
    public void net48_mirror_matches_pc_special_band_host_args_for_host_args(string hostArgs)
    {
        var canonical = PcBandHarnessDiagnostics.OptionsFromHostArgs(hostArgs);
        Assert.NotNull(canonical);
        var (mirrorStart, mirrorEnd) = PcSpecialBandLiveRateCardHelperNet48Mirror.ResolvePcNormalHarnessWindow(hostArgs, bandMode: null);
        Assert.Equal(canonical!.PcNormalStart, mirrorStart);
        Assert.Equal(canonical.PcNormalEnd, mirrorEnd);
    }

    [Fact]
    public void net48_mirror_matches_pc_special_band_host_args_for_night_env_without_host_args()
    {
        var canonical = PcSpecialBandHarnessSchedule.OptionsForBand("night");
        var (mirrorStart, mirrorEnd) = PcSpecialBandLiveRateCardHelperNet48Mirror
            .ResolvePcNormalHarnessWindow(hostArgs: null, bandMode: "night");
        Assert.Equal(canonical.PcNormalStart, mirrorStart);
        Assert.Equal(canonical.PcNormalEnd, mirrorEnd);
    }
}
#endif