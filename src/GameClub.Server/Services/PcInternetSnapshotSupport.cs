using System.Text.Json;
using GameClub.Billing;

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

    public const long DefaultSurchargeToman = 20_000L;

    /// <summary>
    /// Builds schema v2 JSON for an open PC session from that session's own legacy snapshot
    /// (never from current branch rate_lines).
    /// </summary>
    public static string RewriteOpenSessionSnapshotJson(string legacyJson)
    {
        using var doc = JsonDocument.Parse(legacyJson);
        var root = doc.RootElement;
        if (root.TryGetProperty("SchemaVersion", out var sv) && sv.GetInt32() == PcRateCardSupport.SnapshotSchemaVersion)
        {
            return legacyJson;
        }

        var surcharge = DefaultSurchargeToman;
        if (root.TryGetProperty("PcSpecialBandSurchargeToman", out var sc))
        {
            surcharge = sc.GetInt64();
        }

        var step = root.GetProperty("StepToman").GetInt64();
        var direction = root.GetProperty("Direction").GetString()!;
        var legacyLines = new List<SnapshotLine>();
        if (root.TryGetProperty("Lines", out var linesEl))
        {
            foreach (var line in linesEl.EnumerateArray())
            {
                legacyLines.Add(ReadLine(line));
            }
        }

        var output = new List<SnapshotLine>();
        foreach (var line in legacyLines)
        {
            if (PcRateCardSupport.IsPcGroup(line.GroupId))
            {
                continue;
            }

            output.Add(line);
        }

        foreach (var groupId in new[] { PilotSeed.PcNormalGroupId, PilotSeed.PcVipGroupId })
        {
            var dayRate = ResolvePcDayRateToman(legacyLines, groupId);
            if (dayRate is not long day)
            {
                continue;
            }

            output.Add(new SnapshotLine(groupId, nameof(PlayMode.Online), null, false, day));
            output.Add(new SnapshotLine(groupId, nameof(PlayMode.Online), null, true, day + surcharge));

            var offDay = legacyLines.FirstOrDefault(l =>
                l.GroupId == groupId
                && l.PlayMode == nameof(PlayMode.Offline)
                && l.PlayerCount == 1
                && !l.IsSpecialBand);
            var offNight = legacyLines.FirstOrDefault(l =>
                l.GroupId == groupId
                && l.PlayMode == nameof(PlayMode.Offline)
                && l.PlayerCount == 1
                && l.IsSpecialBand);
            if (offDay is not null)
            {
                output.Add(offDay);
            }

            if (offNight is not null)
            {
                output.Add(offNight);
            }
            else
            {
                output.Add(new SnapshotLine(groupId, nameof(PlayMode.Offline), 1, true, day + surcharge));
            }
        }

        var payload = new
        {
            SchemaVersion = PcRateCardSupport.SnapshotSchemaVersion,
            StepToman = step,
            Direction = direction,
            Lines = output.Select(l => new
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

    /// <summary>Reverts a v2 open-session snapshot to legacy v1 (Down migration).</summary>
    public static string RevertOpenSessionSnapshotJson(string v2Json, long surchargeToman)
    {
        using var doc = JsonDocument.Parse(v2Json);
        var root = doc.RootElement;
        if (root.TryGetProperty("SchemaVersion", out var sv) && sv.GetInt32() != PcRateCardSupport.SnapshotSchemaVersion)
        {
            return v2Json;
        }

        var step = root.GetProperty("StepToman").GetInt64();
        var direction = root.GetProperty("Direction").GetString()!;
        var lines = new List<SnapshotLine>();
        if (root.TryGetProperty("Lines", out var linesEl))
        {
            foreach (var line in linesEl.EnumerateArray())
            {
                lines.Add(ReadLine(line));
            }
        }

        var output = new List<SnapshotLine>();
        foreach (var line in lines)
        {
            if (PcRateCardSupport.IsPcGroup(line.GroupId)
                && line.PlayMode == nameof(PlayMode.Online)
                && line.IsSpecialBand)
            {
                continue;
            }

            if (PcRateCardSupport.IsPcGroup(line.GroupId)
                && line.PlayMode == nameof(PlayMode.Online)
                && !line.IsSpecialBand)
            {
                output.Add(new SnapshotLine(line.GroupId, nameof(PlayMode.Offline), 1, false, line.HourlyToman));
                continue;
            }

            output.Add(line);
        }

        foreach (var groupId in new[] { PilotSeed.PcNormalGroupId, PilotSeed.PcVipGroupId })
        {
            if (output.Any(l => l.GroupId == groupId && l.PlayMode == nameof(PlayMode.Offline) && l.IsSpecialBand))
            {
                continue;
            }

            var day = output.FirstOrDefault(l =>
                l.GroupId == groupId && l.PlayMode == nameof(PlayMode.Offline) && !l.IsSpecialBand);
            if (day is not null)
            {
                output.Add(new SnapshotLine(groupId, nameof(PlayMode.Offline), 1, true, day.HourlyToman + surchargeToman));
            }
        }

        var payload = new
        {
            StepToman = step,
            Direction = direction,
            PcSpecialBandSurchargeToman = surchargeToman,
            Lines = output.Select(l => new
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

    private static long? ResolvePcDayRateToman(IReadOnlyList<SnapshotLine> lines, string groupId)
    {
        var offline = lines.FirstOrDefault(l =>
            l.GroupId == groupId
            && l.PlayMode == nameof(PlayMode.Offline)
            && l.PlayerCount == 1
            && !l.IsSpecialBand);
        if (offline is not null)
        {
            return offline.HourlyToman;
        }

        var online = lines.FirstOrDefault(l =>
            l.GroupId == groupId
            && l.PlayMode == nameof(PlayMode.Online)
            && l.PlayerCount is null
            && !l.IsSpecialBand);
        return online?.HourlyToman;
    }

    private static SnapshotLine ReadLine(JsonElement line)
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

        return new SnapshotLine(groupId, playMode, playerCount, isSpecial, hourly);
    }
}