using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed record BranchRatesSnapshot(RateCard Card, RoundingPolicy Rounding);

public sealed class RateConfigService(AppDbContext db, AuditService audit)
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

        var card = BuildCard(lines);
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
        return new RateConfigResponse(roundingRow.StepToman, roundingRow.Direction, lines);
    }

    public async Task UpdateAsync(RateConfigUpdateRequest request, CancellationToken ct = default)
    {
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

    private static RoundingPolicy BuildRounding(BranchRoundingEntity row)
    {
        var direction = row.Direction.Equals("Down", StringComparison.OrdinalIgnoreCase)
            ? RoundingDirection.Down
            : RoundingDirection.Up;
        return RoundingPolicy.Create(row.StepToman, direction, RoundingScope.WholeInvoice);
    }

    private static RateCard BuildCard(IReadOnlyList<RateLineEntity> lines)
    {
        var builder = new RateCardBuilder()
            .AddBand(GameTimerReference.SpecialHours)
            .AddBand(PilotSeed.PcNormalSpecialHours)
            .AddBand(PilotSeed.PcVipSpecialHours);

        foreach (var line in lines.Where(l => !l.IsSpecialBand))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            builder.AddBase(line.GroupId, mode, line.PlayerCount, line.HourlyToman);
        }

        foreach (var line in lines.Where(l => l.IsSpecialBand))
        {
            var mode = Enum.Parse<PlayMode>(line.PlayMode, true);
            var bandId = line.GroupId switch
            {
                PilotSeed.PcNormalGroupId => PilotSeed.PcNormalBandId,
                PilotSeed.PcVipGroupId => PilotSeed.PcVipBandId,
                GameTimerReference.Ps5GroupId => GameTimerReference.SpecialBandId,
                _ => throw new InvalidOperationException($"Unknown group for special band: {line.GroupId}"),
            };
            builder.AddBandRate(bandId, line.GroupId, mode, line.PlayerCount, line.HourlyToman);
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
            Base(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalHourly),
            Special(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, PilotSeed.PcNormalSpecialHourly),
            Base(PilotSeed.PcVipGroupId, PlayMode.Offline, 1, PilotSeed.PcVipHourly),
            Special(PilotSeed.PcVipGroupId, PlayMode.Offline, 1, PilotSeed.PcVipSpecialHourly),
        ];
    }
}

public sealed record RateLineResponse(
    string GroupId,
    string PlayMode,
    int? PlayerCount,
    bool IsSpecialBand,
    long HourlyToman);

public sealed record RateConfigResponse(long RoundingStepToman, string RoundingDirection, IReadOnlyList<RateLineResponse> Lines);

public sealed record RateLineUpdate(
    string GroupId,
    string PlayMode,
    int? PlayerCount,
    bool IsSpecialBand,
    long HourlyToman);

public sealed record RateConfigUpdateRequest(long RoundingStepToman, string RoundingDirection, IReadOnlyList<RateLineUpdate> Lines);
