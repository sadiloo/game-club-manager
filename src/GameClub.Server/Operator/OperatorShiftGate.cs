using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorShiftGate(AppDbContext db)
{
    public async Task<BranchCafeSettingsEntity> LoadSettingsAsync(CancellationToken ct)
    {
        var row = await db.BranchCafeSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return row ?? new BranchCafeSettingsEntity();
    }

    public async Task EnsureMoneyRecordingAllowedAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return;
        }

        if (!await db.Shifts.AsNoTracking().AnyAsync(s => s.Status == ShiftStatus.Open, ct))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "no_open_shift",
                "No shift is open. Open a shift before recording money.");
        }
    }

    public async Task<Guid?> RequireOpenShiftIdAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return null;
        }

        var id = await db.Shifts.AsNoTracking()
            .Where(s => s.Status == ShiftStatus.Open)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (id == Guid.Empty)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "no_open_shift",
                "No shift is open.");
        }

        return id;
    }

    public async Task<ShiftEntity?> GetOpenShiftTrackedAsync(CancellationToken ct)
        => await db.Shifts.FirstOrDefaultAsync(s => s.Status == ShiftStatus.Open, ct);

    public async Task<ShiftEntity?> GetOpenShiftForUpdateAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return null;
        }

        try
        {
            await db.Database.ExecuteSqlRawAsync(
                """SELECT "Id" FROM shifts WHERE "Status" = 'Open' FOR UPDATE""");
        }
        catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "shift_busy",
                "Shift is being updated.");
        }

        return await db.Shifts.FirstOrDefaultAsync(s => s.Status == ShiftStatus.Open, ct);
    }

    public async Task EnsureOpenShiftForMoneyAsync(CancellationToken ct)
    {
        var settings = await LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return;
        }

        var shift = await GetOpenShiftForUpdateAsync(ct);
        if (shift is null)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "no_open_shift",
                "No shift is open.");
        }

        if (shift.Status != ShiftStatus.Open)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "shift_closed",
                "Shift is already closed.");
        }
    }
}
