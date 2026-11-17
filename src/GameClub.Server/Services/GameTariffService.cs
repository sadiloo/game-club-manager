using GameClub.Billing;
using GameClub.Server;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed class GameTariffService(AppDbContext db, AuditService audit)
{
    public const string SplitReasonGameChange = "تغییر بازی";

    public static string FormatConsoleOption(PlayMode mode, int? playerCount)
        => mode == PlayMode.Online
            ? "آنلاین"
            : $"آفلاین {playerCount ?? 1} دسته";

    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Replace('ي', 'ی').Replace('ك', 'ک').ToLowerInvariant();
    }

    public async Task<IReadOnlyList<object>> ListSelectableAsync(PlayMode mode, int playerCount, CancellationToken ct)
    {
        var titles = await db.GameTitles.AsNoTracking()
            .Where(g => g.Enabled)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
        var result = new List<object>();
        foreach (var title in titles)
        {
            if (await HasRateAsync(title.Id, mode, playerCount, ct))
            {
                result.Add(new { id = title.Id, name = title.Name });
            }
        }

        return result;
    }

    public async Task<bool> HasRateAsync(int gameTitleId, PlayMode mode, int playerCount, CancellationToken ct)
        => await TryGetLineAsync(gameTitleId, mode, playerCount, allowDisabled: true, ct) is not null;

    public async Task<GameRateLineEntity?> TryGetLineAsync(
        int gameTitleId,
        PlayMode mode,
        int playerCount,
        bool allowDisabled,
        CancellationToken ct)
    {
        var title = await db.GameTitles.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameTitleId, ct);
        if (title is null || (!allowDisabled && !title.Enabled))
        {
            return null;
        }

        return await db.GameRateLines.AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.GameTitleId == gameTitleId
                    && r.PlayMode == mode.ToString()
                    && r.PlayerCount == (mode == PlayMode.Offline ? playerCount : null),
                ct);
    }

    public async Task<(long Hourly, long? Night)> ResolveSnapshotsAsync(
        int gameTitleId,
        PlayMode mode,
        int playerCount,
        CancellationToken ct)
    {
        var title = await db.GameTitles.AsNoTracking().FirstOrDefaultAsync(g => g.Id == gameTitleId, ct);
        var gameName = title?.Name ?? "؟";
        var option = FormatConsoleOption(mode, playerCount);
        var line = await TryGetLineAsync(gameTitleId, mode, playerCount, allowDisabled: true, ct)
            ?? throw new ApiException(
                StatusCodes.Status409Conflict,
                "game_tariff_not_found",
                $"برای بازی «{gameName}» و گزینه «{option}» نرخ تعریف نشده است.");
        if (line.HourlyToman <= 0)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "game_tariff_not_found",
                $"برای بازی «{gameName}» و گزینه «{option}» نرخ تعریف نشده است.");
        }

        if (line.NightHourlyToman is long night && night <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "نرخ شبانه بازی باید خالی یا بیشتر از صفر باشد.");
        }

        return (line.HourlyToman, line.NightHourlyToman);
    }

    public async Task<GameTitleEntity> EnsureGameForSessionAsync(int? gameTitleId, PlayMode mode, int playerCount, CancellationToken ct)
    {
        if (gameTitleId is not int id)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "game_required", "انتخاب بازی الزامی است.");
        }

        var title = await db.GameTitles.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "بازی یافت نشد.");
        var option = FormatConsoleOption(mode, playerCount);
        if (!title.Enabled)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "game_disabled",
                $"بازی «{title.Name}» برای گزینه «{option}» غیرفعال است.");
        }

        _ = await ResolveSnapshotsAsync(id, mode, playerCount, ct);
        return title;
    }

    public void ApplySnapshots(PlaySegmentEntity segment, int gameTitleId, long hourly, long? night)
    {
        segment.GameTitleId = gameTitleId;
        segment.GameHourlyRateToman = hourly;
        segment.GameNightHourlyRateToman = night;
    }

    public void ClearGame(PlaySegmentEntity segment)
    {
        segment.GameTitleId = null;
        segment.GameHourlyRateToman = null;
        segment.GameNightHourlyRateToman = null;
    }

    public async Task<IReadOnlyList<object>> ListTitlesForManagerAsync(CancellationToken ct)
    {
        var keys = await ConsoleRateKeysAsync(ct);
        var titles = await db.GameTitles.Include(g => g.RateLines).OrderBy(g => g.Name).ToListAsync(ct);
        return titles.Select(t => new
        {
            t.Id,
            t.Name,
            t.Enabled,
            rates = keys.Select(k => MapRateRow(t, k.Mode, k.PlayerCount)),
        }).ToList();
    }

    private object? MapRateRow(GameTitleEntity title, PlayMode mode, int? playerCount)
    {
        var line = title.RateLines.FirstOrDefault(r =>
            string.Equals(r.PlayMode, mode.ToString(), StringComparison.Ordinal)
            && r.PlayerCount == playerCount);
        return new
        {
            mode = mode.ToString(),
            playerCount,
            hourlyToman = line?.HourlyToman,
            nightHourlyToman = line?.NightHourlyToman,
        };
    }

    public async Task<IReadOnlyList<(PlayMode Mode, int? PlayerCount)>> ConsoleRateKeysAsync(CancellationToken ct)
    {
        var lines = await db.RateLines.AsNoTracking()
            .Where(r => !r.IsSpecialBand)
            .Join(
                db.StationGroups.AsNoTracking().Where(g => g.Kind == StationKind.Console),
                r => r.GroupId,
                g => g.Id,
                (r, _) => r)
            .ToListAsync(ct);
        return lines
            .Select(r => (
                Enum.Parse<PlayMode>(r.PlayMode, true),
                r.PlayerCount))
            .Distinct()
            .OrderBy(k => k.Item1)
            .ThenBy(k => k.PlayerCount ?? 0)
            .ToList();
    }

    public async Task<GameTitleEntity> SaveTitleAsync(
        int? id,
        string name,
        bool enabled,
        IReadOnlyList<GameRateLineInput> rateLines,
        CancellationToken ct)
    {
        var normalized = NormalizeName(name);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_name", "نام بازی الزامی است.");
        }

        var dupQuery = db.GameTitles.Where(g => g.NameNormalized == normalized);
        if (id is int existingId)
        {
            dupQuery = dupQuery.Where(g => g.Id != existingId);
        }

        var dup = await dupQuery.AnyAsync(ct);
        if (dup)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "duplicate_name", "بازی‌ای با این نام قبلاً ثبت شده است.");
        }

        ValidateRates(rateLines);
        await ValidateConsoleOptionRowsAsync(rateLines, ct);

        GameTitleEntity entity;
        Dictionary<string, (long Hourly, long? Night)> oldRates;
        string? oldName = null;
        if (id is int updateId)
        {
            entity = await db.GameTitles.Include(g => g.RateLines).FirstOrDefaultAsync(g => g.Id == updateId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "بازی یافت نشد.");
            oldName = entity.Name;
            oldRates = entity.RateLines.ToDictionary(
                r => RateLineKey(Enum.Parse<PlayMode>(r.PlayMode, true), r.PlayerCount),
                r => (Hourly: r.HourlyToman, Night: r.NightHourlyToman));
            entity.Name = name.Trim();
            entity.NameNormalized = normalized;
            entity.Enabled = enabled;
            audit.Stage("game_title.update", new { gameTitleId = entity.Id, oldName, name = entity.Name, enabled });
        }
        else
        {
            entity = new GameTitleEntity
            {
                Name = name.Trim(),
                NameNormalized = normalized,
                Enabled = enabled,
            };
            db.GameTitles.Add(entity);
            oldRates = new Dictionary<string, (long Hourly, long? Night)>();
            audit.Stage("game_title.create", new { name = entity.Name, enabled });
        }

        entity.RateLines.Clear();
        foreach (var row in rateLines)
        {
            entity.RateLines.Add(new GameRateLineEntity
            {
                PlayMode = row.Mode.ToString(),
                PlayerCount = row.Mode == PlayMode.Offline ? row.PlayerCount : null,
                HourlyToman = row.HourlyToman,
                NightHourlyToman = row.NightHourlyToman,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await AuditRateChangesStagedAsync(entity.Id, oldRates, rateLines, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        return entity;
    }

    public async Task DeleteTitleIfUnusedAsync(int id, CancellationToken ct)
    {
        var used = await db.Segments.AnyAsync(s => s.GameTitleId == id, ct);
        if (used)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "in_use", "این بازی در جلسه استفاده شده؛ حذف نکنید و غیرفعال کنید.");
        }

        var entity = await db.GameTitles.FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Game not found.");
        db.GameTitles.Remove(entity);
        await audit.AppendAsync("game_title.delete", new { gameTitleId = id, name = entity.Name }, ct);
        await db.SaveChangesAsync(ct);
    }

    private static string RateLineKey(PlayMode mode, int? playerCount)
        => $"{mode}:{playerCount?.ToString() ?? "-"}";

    private async Task ValidateConsoleOptionRowsAsync(IReadOnlyList<GameRateLineInput> rateLines, CancellationToken ct)
    {
        var allowed = (await ConsoleRateKeysAsync(ct))
            .Select(k => (k.Mode, k.PlayerCount))
            .ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rateLines)
        {
            var playerCount = row.Mode == PlayMode.Offline ? row.PlayerCount : null;
            if (row.Mode == PlayMode.Offline && playerCount is not int)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_option", "برای حالت آفلاین باید تعداد بازیکن مشخص شود.");
            }

            if (!allowed.Contains((row.Mode, playerCount)))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "unknown_console_option", "این گزینه در نرخ ایستگاه کنسول تعریف نشده است.");
            }

            var key = RateLineKey(row.Mode, playerCount);
            if (!seen.Add(key))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "duplicate_option", "برای یک گزینه کنسول بیش از یک نرخ ثبت شده است.");
            }
        }
    }

    private Task AuditRateChangesStagedAsync(
        int gameTitleId,
        Dictionary<string, (long Hourly, long? Night)> oldRates,
        IReadOnlyList<GameRateLineInput> newRates,
        CancellationToken ct)
    {
        AuditRateChangesCore(gameTitleId, oldRates, newRates);
        return Task.CompletedTask;
    }

    private void AuditRateChangesCore(
        int gameTitleId,
        Dictionary<string, (long Hourly, long? Night)> oldRates,
        IReadOnlyList<GameRateLineInput> newRates)
    {
        var newMap = newRates.ToDictionary(
            r => RateLineKey(r.Mode, r.Mode == PlayMode.Offline ? r.PlayerCount : null),
            r => (Hourly: r.HourlyToman, Night: r.NightHourlyToman));

        var keys = oldRates.Keys.Union(newMap.Keys).Distinct();
        foreach (var key in keys)
        {
            var hasOld = oldRates.TryGetValue(key, out var oldVal);
            var hasNew = newMap.TryGetValue(key, out var newVal);
            if (hasOld && hasNew && oldVal.Hourly == newVal.Hourly && oldVal.Night == newVal.Night)
            {
                continue;
            }

            if (!hasOld && !hasNew)
            {
                continue;
            }

            ParseRateLineKey(key, out var mode, out var playerCount);
            audit.Stage(
                "game_rate.change",
                new
                {
                    gameTitleId,
                    mode = mode.ToString(),
                    playerCount,
                    oldHourlyToman = hasOld ? oldVal.Hourly : (long?)null,
                    newHourlyToman = hasNew ? newVal.Hourly : (long?)null,
                    oldNightHourlyToman = hasOld ? oldVal.Night : null,
                    newNightHourlyToman = hasNew ? newVal.Night : null,
                });
        }
    }

    private static void ParseRateLineKey(string key, out PlayMode mode, out int? playerCount)
    {
        var split = key.Split(':', 2);
        mode = Enum.Parse<PlayMode>(split[0], true);
        playerCount = split[1] == "-" ? null : int.Parse(split[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ValidateRates(IReadOnlyList<GameRateLineInput> rateLines)
    {
        foreach (var row in rateLines)
        {
            if (row.HourlyToman <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "نرخ ساعتی بازی باید بیشتر از صفر باشد.");
            }

            if (row.NightHourlyToman is long night && night <= 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_rate", "نرخ شبانه بازی باید خالی یا بیشتر از صفر باشد.");
            }
        }
    }
}

public sealed record GameRateLineInput(PlayMode Mode, int? PlayerCount, long HourlyToman, long? NightHourlyToman);