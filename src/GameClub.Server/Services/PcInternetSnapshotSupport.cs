using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Services;

/// <summary>Static helpers for migration-time snapshot v2 rewrites (no DI).</summary>
public static class PcInternetSnapshotSupport
{
    private sealed record SnapshotLine(
        string GroupId,
        string PlayMode,
        int? PlayerCount,
        bool IsSpecialBand,
        long HourlyToman);

    public static string RewriteOpenSessionSnapshotJson(
        string legacyJson,
        IReadOnlyList<RateLineEntity> pcGroupLines)
    {
        using var doc = JsonDocument.Parse(legacyJson);
        var root = doc.RootElement;
        var lines = new List<SnapshotLine>();
        if (root.TryGetProperty("Lines", out var linesEl))
        {
            foreach (var line in linesEl.EnumerateArray())
            {
                var groupId = line.GetProperty("GroupId").GetString()!;
                var playMode = line.GetProperty("PlayMode").GetString()!;
                var playerCount = line.TryGetProperty("PlayerCount", out var pc) && pc.ValueKind != JsonValueKind.Null
                    ? (int?)pc.GetInt32()
                    : null;
                var isSpecial = line.GetProperty("IsSpecialBand").GetBoolean();
                var hourly = line.GetProperty("HourlyToman").GetInt64();
                if (PcRateCardSupport.IsPcGroup(groupId)
                    && playMode == nameof(PlayMode.Offline)
                    && playerCount == 1
                    && !isSpecial)
                {
                    playMode = nameof(PlayMode.Online);
                    playerCount = null;
                }

                lines.Add(new SnapshotLine(groupId, playMode, playerCount, isSpecial, hourly));
            }
        }

        foreach (var dbLine in pcGroupLines.Where(l => PcRateCardSupport.IsPcGroup(l.GroupId)))
        {
            var exists = lines.Any(l =>
                l.GroupId == dbLine.GroupId
                && l.PlayMode == dbLine.PlayMode
                && l.PlayerCount == dbLine.PlayerCount
                && l.IsSpecialBand == dbLine.IsSpecialBand);
            if (!exists)
            {
                lines.Add(new SnapshotLine(
                    dbLine.GroupId,
                    dbLine.PlayMode,
                    dbLine.PlayerCount,
                    dbLine.IsSpecialBand,
                    dbLine.HourlyToman));
            }
        }

        var step = root.GetProperty("StepToman").GetInt64();
        var direction = root.GetProperty("Direction").GetString()!;
        var payload = new
        {
            SchemaVersion = PcRateCardSupport.SnapshotSchemaVersion,
            StepToman = step,
            Direction = direction,
            Lines = lines.Select(l => new
            {
                l.GroupId,
                l.PlayMode,
                l.PlayerCount,
                l.IsSpecialBand,
                l.HourlyToman,
            }),
        };
        return JsonSerializer.Serialize(payload);
    }
}