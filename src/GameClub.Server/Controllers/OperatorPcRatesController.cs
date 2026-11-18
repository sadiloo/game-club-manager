using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/pc-rates")]
public sealed class OperatorPcRatesController(
    AppDbContext db,
    AuditService audit,
    OperatorContext context) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        RequireManager();
        return Ok(await LoadGroupsAsync(ct));
    }

  [HttpGet("{groupId}")]
    public async Task<IActionResult> GetOne(string groupId, CancellationToken ct)
    {
        RequireManager();
        if (!PcRateCardSupport.IsPcGroup(groupId))
        {
            throw new ApiException(StatusCodes.Status404NotFound, "not_found", "گروه یافت نشد.");
        }

        return Ok(await LoadGroupAsync(groupId, ct));
    }

    public sealed record PcRatesBody(long OnlineDay, long OnlineNight, long? OfflineDay, long? OfflineNight);

    [HttpPut("{groupId}")]
    public async Task<IActionResult> Put(string groupId, PcRatesBody body, CancellationToken ct)
    {
        RequireManager();
        if (!PcRateCardSupport.IsPcGroup(groupId))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc_group", "فقط گروه‌های PC مجاز هستند.");
        }

        if (body.OnlineDay < 0 || body.OnlineNight < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "عدد معتبر وارد کنید.");
        }

        if (body.OfflineDay.HasValue != body.OfflineNight.HasValue)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "offline_pair_required", "نرخ آفلاین روز و شب را با هم وارد کنید.");
        }

        var before = await LoadGroupAsync(groupId, ct);
        await UpsertLineAsync(groupId, PlayMode.Online, null, false, body.OnlineDay, ct);
        await UpsertLineAsync(groupId, PlayMode.Online, null, true, body.OnlineNight, ct);
        if (body.OfflineDay is long offDay && body.OfflineNight is long offNight)
        {
            await UpsertLineAsync(groupId, PlayMode.Offline, 1, false, offDay, ct);
            await UpsertLineAsync(groupId, PlayMode.Offline, 1, true, offNight, ct);
        }

        var after = await LoadGroupAsync(groupId, ct);
        await audit.AppendAsync("rate.change", new { groupId, before, after, actor = context.UserId }, ct);
        await db.SaveChangesAsync(ct);
        return Ok(after);
    }

    private void RequireManager()
    {
        if (context.Role is not (UserRole.Manager or UserRole.Owner))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "فقط مدیر می‌تواند نرخ‌های PC را ببیند.");
        }
    }

    private async Task UpsertLineAsync(
        string groupId,
        PlayMode mode,
        int? players,
        bool isSpecial,
        long hourly,
        CancellationToken ct)
    {
        var modeStr = mode.ToString();
        var row = await db.RateLines.FirstOrDefaultAsync(
            l => l.GroupId == groupId && l.PlayMode == modeStr && l.PlayerCount == players && l.IsSpecialBand == isSpecial,
            ct);
        if (row is null)
        {
            db.RateLines.Add(new RateLineEntity
            {
                GroupId = groupId,
                PlayMode = modeStr,
                PlayerCount = players,
                IsSpecialBand = isSpecial,
                HourlyToman = hourly,
            });
        }
        else
        {
            row.HourlyToman = hourly;
        }
    }

    private async Task<IReadOnlyList<object>> LoadGroupsAsync(CancellationToken ct)
    {
        var result = new List<object>();
        foreach (var groupId in new[] { PilotSeed.PcNormalGroupId, PilotSeed.PcVipGroupId })
        {
            result.Add(await LoadGroupAsync(groupId, ct));
        }

        return result;
    }

    private async Task<object> LoadGroupAsync(string groupId, CancellationToken ct)
    {
        var lines = await db.RateLines.AsNoTracking().Where(l => l.GroupId == groupId).ToListAsync(ct);
        var name = groupId == PilotSeed.PcVipGroupId ? "PC VIP" : "PC Normal";
        long? OfflineVal(bool special) => lines
            .FirstOrDefault(l => l.PlayMode == nameof(PlayMode.Offline) && l.IsSpecialBand == special)?.HourlyToman;
        return new
        {
            groupId,
            name,
            onlineDay = lines.FirstOrDefault(l => l.PlayMode == nameof(PlayMode.Online) && !l.IsSpecialBand)?.HourlyToman,
            onlineNight = lines.FirstOrDefault(l => l.PlayMode == nameof(PlayMode.Online) && l.IsSpecialBand)?.HourlyToman,
            offlineDay = OfflineVal(false),
            offlineNight = OfflineVal(true),
        };
    }
}