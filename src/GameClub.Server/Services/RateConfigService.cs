using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GameClub.Server.Services;

public sealed record BranchRatesSnapshot(RateCard Card, RoundingPolicy Rounding);

public sealed class RateConfigService(
    AppDbContext db,
    AuditService audit,
    IOptions<PcSpecialBandHarnessOptions> pcSpecialBandHarness)
{
    public async Task<BranchRatesSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var roundingRow = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        var rounding = BuildRounding(roundingRow);
        var lines = await db.RateLines.AsNoTracking().ToListAsync(ct);
        if (lines.Count == 0)
        {
            return new BranchRatesSnapshot(PilotSeed.CreateCard(), rounding);
        }

        var card = BuildCardFromDbLines(lines);
        return new BranchRatesSnapshot(card, rounding);
    }

    public async Task<RateConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        var roundingRow = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        var lines = await db.RateLines.AsNoTracking()
            .OrderBy(l => l.GroupId).ThenBy(l => l.IsSpecialBand).ThenBy(l => l.PlayMode).ThenBy(l => l.PlayerCount)
            .Select(l => new RateLineResponse(
                l.GroupId,
                l.PlayMode,
                l.PlayerCount,
                l.IsSpecialBand,
                l.HourlyToman))
            .ToListAsync(ct);
        return new RateConfigResponse(
            roundingRow.StepToman,
            roundingRow.Direction,
            lines);
    }

    public async Task UpdateAsync(RateConfigUpdateRequest request, CancellationToken ct = default)
    {
        if (request.RoundingStepToman <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rounding", "Rounding step must be positive.");
        }

        if (!request.RoundingDirection.Equals("Up", StringComparison.OrdinalIgnoreCase)
            && !request.RoundingDirection.Equals("Down", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rounding", "Rounding direction must be Up or Down.");
        }

        foreach (var line in request.Lines)
        {
            if (line.HourlyToman < 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "Hourly rate cannot be negative.");
            }
        }

        var rounding = await db.BranchRounding.SingleAsync(ct);
        rounding.StepToman = request.RoundingStepToman;
        rounding.Direction = request.RoundingDirection;

        foreach (var line in request.Lines)
        {
            var row = await db.RateLines.SingleOrDefaultAsync(
                l => l.GroupId == line.GroupId
                    && l.PlayMode == line.PlayMode
                    && l.PlayerCount == line.PlayerCount
                    && l.IsSpecialBand == line.IsSpecialBand,
                ct);
            if (row is null)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "unknown_rate_line",
                    $"No rate line for group={line.GroupId} mode={line.PlayMode} players={line.PlayerCount} special={line.IsSpecialBand}.");
            }

            row.HourlyToman = line.HourlyToman;
        }

        await audit.AppendAsync("rate.change", new { request.RoundingStepToman, lineCount = request.Lines.Count }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<BranchRatesSnapshot> LoadSnapshotAsync(int snapshotId, CancellationToken ct = default)
    {
        var row = await db.RateCardSnapshots.AsNoTracking().SingleAsync(s => s.Id == snapshotId, ct);
        return DeserializeSnapshot(row.Json);
    }

    public async Task<RateCardSnapshotEntity> CreateSnapshotEntityAsync(CancellationToken ct = default)
    {
        var roundingRow = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        var lines = await db.RateLines.AsNoTracking().ToListAsync(ct);
        var payload = new StoredBranchRatesV2(
            PcRateCardSupport.SnapshotSchemaVersion,
            roundingRow.StepToman,
            roundingRow.Direction,
            lines.Select(l => new StoredRateLine(l.GroupId, l.PlayMode, l.PlayerCount, l.IsSpecialBand, l.HourlyToman)).ToList());
        return new RateCardSnapshotEntity { Json = JsonSerializer.Serialize(payload) };
    }

    internal BranchRatesSnapshot DeserializeSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var schemaVersion = root.TryGetProperty("SchemaVersion", out var sv) ? sv.GetInt32() : (int?)null;
        var step = root.GetProperty("StepToman").GetInt64();
        var direction = root.GetProperty("Direction").GetString()!;
        if (schemaVersion == PcRateCardSupport.SnapshotSchemaVersion)
        {
            var lines = root.GetProperty("Lines").Deserialize<List<StoredRateLine>>(JsonSerializerOptions.Web)
                ?? throw new InvalidOperationException("Invalid rate snapshot lines.");
            var entities = lines.Select(l => new RateLineEntity
            {
                GroupId = l.GroupId,
                PlayMode = l.PlayMode,
                PlayerCount = l.PlayerCount,
                IsSpecialBand = l.IsSpecialBand,
                HourlyToman = l.HourlyToman,
            }).ToList();
            var rounding = BuildRounding(new BranchRoundingEntity { StepToman = step, Direction = direction });
            return new BranchRatesSnapshot(BuildCardFromDbLines(entities), rounding);
        }

        long surcharge = 20_000;
        if (root.TryGetProperty("PcSpecialBandSurchargeToman", out var sc))
        {
            surcharge = sc.GetInt64();
        }

        var legacyLines = root.GetProperty("Lines").Deserialize<List<StoredRateLine>>(JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Invalid rate snapshot lines.");
        var legacyEntities = legacyLines.Select(l => new RateLineEntity
        {
            GroupId = l.GroupId,
            PlayMode = l.PlayMode,
            PlayerCount = l.PlayerCount,
            IsSpecialBand = l.IsSpecialBand,
            HourlyToman = l.HourlyToman,
        }).ToList();
        var legacyRounding = BuildRounding(new BranchRoundingEntity { StepToman = step, Direction = direction });
        return new BranchRatesSnapshot(BuildCardLegacy(legacyEntities, surcharge), legacyRounding);
    }

    private sealed record StoredBranchRatesV2(
        int SchemaVersion,
        long StepToman,
        string Direction,
        IReadOnlyList<StoredRateLine> Lines);

    private sealed record StoredRateLine(
        string GroupId,
        string PlayMode,
        int? PlayerCount,
        bool IsSpecialBand,
        long HourlyToman);

    private static RoundingPolicy BuildRounding(BranchRoundingEntity row)
    {
        var direction = row.Direction.Equals("Down", StringComparison.OrdinalIgnoreCase)
            ? RoundingDirection.Down
            : RoundingDirection.Up;
        return RoundingPolicy.Create(row.StepToman, direction, RoundingScope.WholeInvoice);
    }

    private RateCard BuildCardFromDbLines(IReadOnlyList<RateLineEntity> lines)
    {
        var builder = new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBand(PcSpecialBandHarness.ResolvePcNormalBand(pcSpecialBandHarness))
            .AddBand(PcSpecialBandHarness.ResolvePcVipBand(pcSpecialBandHarness));

        foreach (var line in lines.Where(l => !l.IsSpecialBand))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            builder.AddBase(line.GroupId, mode, line.PlayerCount, line.HourlyToman);
        }

        foreach (var line in lines.Where(l => l.IsSpecialBand))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            if (PcRateCardSupport.IsPcGroup(line.GroupId))
            {
                builder.AddBandRate(
                    PcRateCardSupport.BandIdForGroup(line.GroupId),
                    line.GroupId,
                    mode,
                    line.PlayerCount,
                    line.HourlyToman);
            }
            else if (line.GroupId == GameTimerReference.Ps5GroupId)
            {
                builder.AddBandRate(
                    GameTimerReference.SpecialBandId,
                    line.GroupId,
                    mode,
                    line.PlayerCount,
                    line.HourlyToman);
            }
        }

        return builder.Build();
    }

    private RateCard BuildCardLegacy(IReadOnlyList<RateLineEntity> lines, long pcSpecialSurchargeToman)
    {
        var builder = new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBand(PcSpecialBandHarness.ResolvePcNormalBand(pcSpecialBandHarness))
            .AddBand(PcSpecialBandHarness.ResolvePcVipBand(pcSpecialBandHarness));

        foreach (var line in lines.Where(l => !l.IsSpecialBand))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            builder.AddBase(line.GroupId, mode, line.PlayerCount, line.HourlyToman);
            if (PcRateCardSupport.IsPcGroup(line.GroupId))
            {
                var bandId = PcRateCardSupport.BandIdForGroup(line.GroupId);
                builder.AddBandRate(
                    bandId,
                    line.GroupId,
                    mode,
                    line.PlayerCount,
                    line.HourlyToman + pcSpecialSurchargeToman);
            }
        }

        foreach (var line in lines.Where(l => l.IsSpecialBand && l.GroupId == GameTimerReference.Ps5GroupId))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            builder.AddBandRate(
                GameTimerReference.SpecialBandId,
                line.GroupId,
                mode,
                line.PlayerCount,
                line.HourlyToman);
        }

        return builder.Build();
    }

    public static IReadOnlyList<RateLineEntity> PilotSeedLines()
    {
        static RateLineEntity Base(string groupId, PlayMode mode, int? players, long toman) => new()
        {
            GroupId = groupId,
            PlayMode = mode.ToString(),
            PlayerCount = players,
            IsSpecialBand = false,
            HourlyToman = toman,
        };

        static RateLineEntity Special(string groupId, PlayMode mode, int? players, long toman) => new()
        {
            GroupId = groupId,
            PlayMode = mode.ToString(),
            PlayerCount = players,
            IsSpecialBand = true,
            HourlyToman = toman,
        };

        var ps5 = GameTimerReference.Ps5GroupId;
        return
        [
            Base(ps5, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerHourly),
            Base(ps5, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerHourly),
            Base(ps5, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerHourly),
            Base(ps5, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerHourly),
            Base(ps5, PlayMode.Online, null, GameTimerReference.Ps5OnlineHourly),
            Special(ps5, PlayMode.Offline, 1, GameTimerReference.Ps5OnePlayerSpecialHourly),
            Special(ps5, PlayMode.Offline, 2, GameTimerReference.Ps5TwoPlayerSpecialHourly),
            Special(ps5, PlayMode.Offline, 3, GameTimerReference.Ps5ThreePlayerSpecialHourly),
            Special(ps5, PlayMode.Offline, 4, GameTimerReference.Ps5FourPlayerSpecialHourly),
            Special(ps5, PlayMode.Online, null, GameTimerReference.Ps5OnlineSpecialHourly),
            Base(PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalHourly),
            Special(PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalSpecialHourly),
            Base(PilotSeed.PcVipGroupId, PlayMode.Online, null, PilotSeed.PcVipHourly),
            Special(PilotSeed.PcVipGroupId, PlayMode.Online, null, PilotSeed.PcVipSpecialHourly),
        ];
    }
}

public sealed record RateLineResponse(
    string GroupId,
    string PlayMode,
    int? PlayerCount,
    bool IsSpecialBand,
    long HourlyToman);

public sealed record RateConfigResponse(
    long RoundingStepToman,
    string RoundingDirection,
    IReadOnlyList<RateLineResponse> Lines);

public sealed record RateLineUpdate(
    string GroupId,
    string PlayMode,
    int? PlayerCount,
    bool IsSpecialBand,
    long HourlyToman);

public sealed record RateConfigUpdateRequest(
    long RoundingStepToman,
    string RoundingDirection,
    IReadOnlyList<RateLineUpdate> Lines);