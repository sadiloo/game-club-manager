using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskFreeUseReportService(AppDbContext db, OperatorContext op, IClock clock)
{
    public async Task<object> ReportAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "rep.freeUse.forbidden");
        }

        if (fromUtc > toUtc)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "bad_range", "rep.freeUse.badRange");
        }

        var entities = await db.KioskFreeUses.AsNoTracking()
            .Where(r => r.StartedAtUtc >= fromUtc && r.StartedAtUtc < toUtc)
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(ct);

        var deviceIds = entities.Select(r => r.KioskDeviceId).Distinct().ToList();
        var lastContactByDevice = deviceIds.Count == 0
            ? new Dictionary<Guid, DateTime?>()
            : await db.KioskDevices.AsNoTracking()
                .Where(d => deviceIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.LastSeenUtc, ct);

        var nowUtc = clock.UtcNow.UtcDateTime;
        var rows = new List<object>();
        long totalSeconds = 0;
        var openLiveCount = 0;
        var noContactCount = 0;
        var hasRestartEnd = false;

        foreach (var entity in entities)
        {
            var lastSeen = lastContactByDevice.GetValueOrDefault(entity.KioskDeviceId);
            var view = KioskFreeUseReadModel.ForReportRow(entity, lastSeen, nowUtc);
            totalSeconds += view.DurationSeconds;

            if (entity.EndedAtUtc is null)
            {
                if (view.NoContact)
                {
                    noContactCount++;
                }
                else
                {
                    openLiveCount++;
                }
            }

            if (string.Equals(entity.EndReason, "restart", StringComparison.Ordinal))
            {
                hasRestartEnd = true;
            }

            rows.Add(new
            {
                stationId = view.StationId,
                actorRole = view.ActorRole,
                startedAtUtc = view.StartedAtUtc,
                endedAtUtc = view.EndedAtUtc,
                endReason = view.EndReason,
                lastContactUtc = view.LastContactUtc,
                noContact = view.NoContact,
                durationSeconds = view.DurationSeconds,
            });
        }

        return new
        {
            rows,
            count = rows.Count,
            totalDurationSeconds = totalSeconds,
            openLiveCount,
            noContactCount,
            hasRestartEnd,
        };
    }
}