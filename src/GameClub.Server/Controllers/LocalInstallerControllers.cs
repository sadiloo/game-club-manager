using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/local")]
public sealed class LocalInstallerController(AppDbContext db) : ControllerBase
{
    [HttpGet("open-sessions")]
    public async Task<IActionResult> OpenSessions(CancellationToken ct)
    {
        var openSessions = await db.Sessions.AsNoTracking()
            .Where(s => s.Status == SessionStatus.Open)
            .Select(s => new { s.Id, s.StationId, s.StartedAtUtc })
            .ToListAsync(ct);

        var openFreeUseCount = await db.KioskFreeUses.AsNoTracking()
            .CountAsync(r => r.EndedAtUtc == null, ct);

        return Ok(new
        {
            count = openSessions.Count,
            sessions = openSessions,
            openFreeUses = openFreeUseCount,
        });
    }
}