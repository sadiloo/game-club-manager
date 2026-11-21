#if NET48
using System;
using GameClub.Billing;

namespace GameClub.Kiosk.Windows.IntegrationTests;

/// <summary>Mirror live TestHost PC special-band harness for in-process rate-card estimates (net48 WPF).</summary>
internal static class PcSpecialBandLiveRateCardHelper
{
    public static TimeBand HarnessPcNormalSpecialBandForLiveServer()
    {
        var hostArgs = Environment.GetEnvironmentVariable("GAMECLUB_SERVER_HOST_ARGS");
        var bandMode = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_PC_BAND");
        if (bandMode != null)
        {
            bandMode = bandMode.Trim();
        }

        var (startText, endText) = PcSpecialBandLiveRateCardHelperNet48Mirror.ResolvePcNormalHarnessWindow(hostArgs, bandMode);
        var start = PcSpecialBandLiveRateCardHelperNet48Mirror.ParseTehranTime(startText);
        var end = PcSpecialBandLiveRateCardHelperNet48Mirror.ParseTehranTime(endText);
        return new TimeBand(
            PilotSeed.PcNormalBandId,
            PilotSeed.PcNormalGroupId,
            $"Harness PC special hours {startText}–{endText}",
            start,
            end);
    }
}
#endif