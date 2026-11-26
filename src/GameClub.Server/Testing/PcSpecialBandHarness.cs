using GameClub.Billing;
using Microsoft.Extensions.Options;

namespace GameClub.Server.Testing;

internal static class PcSpecialBandHarness
{
    public static TimeBand ResolvePcNormalBand(IOptions<PcSpecialBandHarnessOptions> harness)
        => ResolveGroupBand(
            harness.Value,
            PilotSeed.PcNormalGroupId,
            PilotSeed.PcNormalBandId,
            PilotSeed.PcNormalSpecialHours,
            harness.Value.PcNormalStart,
            harness.Value.PcNormalEnd);

    public static TimeBand ResolvePcVipBand(IOptions<PcSpecialBandHarnessOptions> harness)
        => ResolveGroupBand(
            harness.Value,
            PilotSeed.PcVipGroupId,
            PilotSeed.PcVipBandId,
            PilotSeed.PcVipSpecialHours,
            harness.Value.PcVipStart,
            harness.Value.PcVipEnd);

    private static TimeBand ResolveGroupBand(
        PcSpecialBandHarnessOptions options,
        string groupId,
        string bandId,
        TimeBand productionDefault,
        string? startRaw,
        string? endRaw)
    {
        if (!options.Enabled
            || string.IsNullOrWhiteSpace(startRaw)
            || string.IsNullOrWhiteSpace(endRaw))
        {
            return productionDefault;
        }

        var start = ParseTehranTime(startRaw);
        var end = ParseTehranTime(endRaw);
        return new TimeBand(
            bandId,
            groupId,
            $"Harness PC special hours {startRaw}–{endRaw}",
            start,
            end);
    }

    internal static TimeOfDay ParseTehranTime(string raw)
    {
        var parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var hour)
            || !int.TryParse(parts[1], out var minute))
        {
            throw new FormatException($"Invalid harness time '{raw}'; expected HH:mm.");
        }

        return new TimeOfDay(hour, minute);
    }
}