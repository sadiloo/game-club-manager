using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Services;

namespace GameClub.Server.Tests;

public sealed class PcInternetSnapshotSupportTests
{
    [Fact]
    public void RewriteOpenSessionSnapshot_uses_session_day_rate_not_global_after_edit()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            StepToman = 5_000,
            Direction = "Up",
            PcSpecialBandSurchargeToman = 25_000L,
            Lines = new[]
            {
                new { GroupId = PilotSeed.PcNormalGroupId, PlayMode = "Offline", PlayerCount = 1, IsSpecialBand = false, HourlyToman = 80_000L },
                new { GroupId = PilotSeed.PcNormalGroupId, PlayMode = "Offline", PlayerCount = 1, IsSpecialBand = true, HourlyToman = 105_000L },
                new { GroupId = PilotSeed.PcVipGroupId, PlayMode = "Offline", PlayerCount = 1, IsSpecialBand = false, HourlyToman = 110_000L },
                new { GroupId = PilotSeed.PcVipGroupId, PlayMode = "Offline", PlayerCount = 1, IsSpecialBand = true, HourlyToman = 135_000L },
            },
        });
        var v2 = PcInternetSnapshotSupport.RewriteOpenSessionSnapshotJson(legacy);
        using var doc = JsonDocument.Parse(v2);
        var lines = doc.RootElement.GetProperty("Lines").EnumerateArray().ToList();
        var normalDay = lines.Single(l =>
            l.GetProperty("GroupId").GetString() == PilotSeed.PcNormalGroupId
            && l.GetProperty("PlayMode").GetString() == "Online"
            && !l.GetProperty("IsSpecialBand").GetBoolean());
        Assert.Equal(80_000, normalDay.GetProperty("HourlyToman").GetInt64());
        var normalNight = lines.Single(l =>
            l.GetProperty("GroupId").GetString() == PilotSeed.PcNormalGroupId
            && l.GetProperty("PlayMode").GetString() == "Online"
            && l.GetProperty("IsSpecialBand").GetBoolean());
        Assert.Equal(105_000, normalNight.GetProperty("HourlyToman").GetInt64());
    }

    [Fact]
    public void SegmentKeySupport_thirty_splits_are_unique()
    {
        var keys = Enumerable.Range(0, 30).Select(SegmentKeySupport.Allocate).ToList();
        Assert.Equal(30, keys.Distinct().Count());
    }
}