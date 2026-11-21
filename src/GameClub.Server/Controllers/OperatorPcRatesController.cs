using System.Text.Json;
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

    [HttpPut("{groupId}")]
    public async Task<IActionResult> Put(string groupId, [FromBody] JsonElement body, CancellationToken ct)
    {
        RequireManager();
        if (!PcRateCardSupport.IsPcGroup(groupId))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_pc_group", "فقط گروه‌های PC مجاز هستند.");
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "عدد معتبر وارد کنید.");
        }

        if (!body.TryGetProperty("onlineDay", out var onlineDayEl) || !body.TryGetProperty("onlineNight", out var onlineNightEl))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "عدد معتبر وارد کنید.");
        }

        var onlineDay = ParseRequiredPcRate(onlineDayEl);
        var onlineNight = ParseRequiredPcRate(onlineNightEl);
        long? offlineDay = null;
        long? offlineNight = null;
        if (body.TryGetProperty("offlineDay", out var offDayEl))
        {
            offlineDay = GameRateTomanParser.ParseOptional(offDayEl, "عدد معتبر وارد کنید.");
        }

        if (body.TryGetProperty("offlineNight", out var offNightEl))
        {
            offlineNight = GameRateTomanParser.ParseOptional(offNightEl, "عدد معتبر وارد کنید.");
        }

        if (offlineDay.HasValue != offlineNight.HasValue)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "offline_pair_required", "نرخ آفلاین روز و شب را با هم وارد کنید.");
        }

        var before = await LoadGroupAsync(groupId, ct);
        await UpsertLineAsync(groupId, PlayMode.Online, null, false, onlineDay, ct);
        await UpsertLineAsync(groupId, PlayMode.Online, null, true, onlineNight, ct);
        if (offlineDay is long offDay && offlineNight is long offNight)
        {
            ValidatePcRate(offDay);
            ValidatePcRate(offNight);
            await UpsertLineAsync(groupId, PlayMode.Offline, 1, false, offDay, ct);
            await UpsertLineAsync(groupId, PlayMode.Offline, 1, true, offNight, ct);
        }

        await db.SaveChangesAsync(ct);
        var after = await LoadGroupAsync(groupId, ct);
        await audit.AppendAsync("rate.change", new { groupId, before, after, actor = context.UserId }, ct);
        await db.SaveChangesAsync(ct);
        return Ok(after);
    }

    private static long ParseRequiredPcRate(JsonElement value)
    {
        var toman = GameRateTomanParser.ParseRequired(value, "عدد معتبر وارد کنید.");
        ValidatePcRate(toman);
        return toman;
    }

    private static void ValidatePcRate(long toman)
    {
        if (toman > OperatorMoneyValidation.MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "عدد معتبر وارد کنید.");
        }
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