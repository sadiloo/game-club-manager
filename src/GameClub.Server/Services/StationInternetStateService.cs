using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Services;

public sealed class StationInternetStateService(AppDbContext db, IClock clock)
{
    public async Task<StationInternetStateEntity> EnsureRowAsync(string stationId, CancellationToken ct)
    {
        var row = await db.StationInternetStates.FirstOrDefaultAsync(s => s.StationId == stationId, ct);
        if (row is not null)
        {
            return row;
        }

        row = new StationInternetStateEntity
        {
            StationId = stationId,
            Epoch = Guid.NewGuid(),
            DesiredOn = true,
            DesiredVersion = 0,
            DesiredAtUtc = clock.UtcNow.UtcDateTime,
            DesiredReason = "session_start",
        };
        db.StationInternetStates.Add(row);
        return row;
    }

    public async Task<long> BumpDesiredAsync(
        string stationId,
        bool desiredOn,
        DateTime atUtc,
        Guid? userId,
        string reason,
        Guid? sessionId,
        CancellationToken ct)
    {
        var row = await EnsureRowAsync(stationId, ct);
        var bump = !row.DesiredOn && desiredOn || row.DesiredVersion == 0 && row.DesiredAtUtc == default;
        if (row.DesiredOn != desiredOn || bump)
        {
            row.DesiredVersion++;
        }

        row.DesiredOn = desiredOn;
        row.DesiredAtUtc = atUtc;
        row.DesiredByUserId = userId;
        row.DesiredReason = reason;
        row.SessionId = sessionId;
        return row.DesiredVersion;
    }

    public static bool IsPending(StationInternetStateEntity row)
        => row.AppliedVersion is null || row.AppliedVersion < row.DesiredVersion;

    public async Task<bool> ApplyReportAsync(
        string stationId,
        Guid epoch,
        long version,
        bool on,
        DateTime? appliedAtUtc,
        string? error,
        CancellationToken ct)
    {
        var row = await db.StationInternetStates.FirstOrDefaultAsync(s => s.StationId == stationId, ct);
        if (row is null)
        {
            return false;
        }

        if (row.Epoch != epoch)
        {
            return false;
        }

        if (row.AppliedVersion is long applied && version <= applied)
        {
            return false;
        }

        row.AppliedVersion = version;
        row.AppliedOn = on;
        row.AppliedAtUtc = appliedAtUtc;
        row.LastError = string.IsNullOrEmpty(error) ? null : error;
        row.LastReportUtc = clock.UtcNow.UtcDateTime;
        return true;
    }
}