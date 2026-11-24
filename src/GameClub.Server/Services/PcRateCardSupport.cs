using GameClub.Billing;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Services;

public static class PcRateCardSupport
{
    public const int SnapshotSchemaVersion = 2;

    public static bool IsPcGroup(string groupId)
        => groupId is PilotSeed.PcNormalGroupId or PilotSeed.PcVipGroupId;

    public static string BandIdForGroup(string groupId)
        => groupId == PilotSeed.PcNormalGroupId ? PilotSeed.PcNormalBandId : PilotSeed.PcVipBandId;

    public static bool HasOfflineRates(RateCard card, string groupId)
    {
        if (!IsPcGroup(groupId))
        {
            return true;
        }

        var key = TariffKey.Create(groupId, PlayMode.Offline, 1);
        if (!card.TryBase(key, out _))
        {
            return false;
        }

        return card.TryBand(BandIdForGroup(groupId), key, out _);
    }

    public static bool HasOfflineRates(IReadOnlyList<RateLineEntity> lines, string groupId)
    {
        var hasDay = lines.Any(l =>
            l.GroupId == groupId
            && l.PlayMode == nameof(PlayMode.Offline)
            && l.PlayerCount == 1
            && !l.IsSpecialBand);
        var hasNight = lines.Any(l =>
            l.GroupId == groupId
            && l.PlayMode == nameof(PlayMode.Offline)
            && l.PlayerCount == 1
            && l.IsSpecialBand);
        return hasDay && hasNight;
    }
}