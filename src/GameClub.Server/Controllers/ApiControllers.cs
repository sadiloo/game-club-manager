using GameClub.Server.Data;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/stations")]
public sealed class StationsController(StationService stations) : ControllerBase
{
    public sealed record RegisterRequest(string Id, string Name, string GroupId);

    public sealed record UpdateGroupRequest(string GroupId);

    public sealed record UpdateStationRequest(string? Name, string? GroupId);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await stations.ListAsync(ct);
        return Ok(rows.Select(s => new { s.Id, s.Name, s.GroupId, s.IsActive }));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var station = await stations.GetAsync(id, ct);
        return Ok(new { station.Id, station.Name, station.GroupId, station.IsActive });
    }

    [HttpPost]
    public async Task<IActionResult> Register(RegisterRequest body, CancellationToken ct)
    {
        var station = await stations.RegisterAsync(body.Id, body.Name, body.GroupId, ct);
        return Created($"/api/stations/{station.Id}", new { station.Id, station.Name, station.GroupId, station.IsActive });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, UpdateStationRequest body, CancellationToken ct)
    {
        var station = await stations.UpdateAsync(id, body.Name, body.GroupId, ct);
        return Ok(new { station.Id, station.Name, station.GroupId, station.IsActive });
    }

    [HttpPatch("{id}/group")]
    public async Task<IActionResult> UpdateGroup(string id, UpdateGroupRequest body, CancellationToken ct)
    {
        var station = await stations.UpdateGroupAsync(id, body.GroupId, ct);
        return Ok(new { station.Id, station.GroupId });
    }

    [HttpPost("{id}/deactivate")]
    public async Task<IActionResult> Deactivate(string id, CancellationToken ct)
    {
        var station = await stations.DeactivateAsync(id, ct);
        return Ok(new { station.Id, station.IsActive });
    }
}

[ApiController]
[Route("api/rates")]
public sealed class RatesController(RateConfigService rates) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var config = await rates.GetConfigAsync(ct);
        return Ok(config);
    }

    [HttpPut]
    public async Task<IActionResult> Update(RateConfigUpdateRequest body, CancellationToken ct)
    {
        await rates.UpdateAsync(body, ct);
        var config = await rates.GetConfigAsync(ct);
        return Ok(config);
    }
}

[ApiController]
[Route("api/sessions")]
public sealed class SessionsController(SessionService sessions, IDeadPcMonitor deadPc) : ControllerBase
{
    public sealed record StartRequest(string StationId, int PlayerCount = 1, string Mode = "Offline", DateTimeOffset? StartedAt = null);

    public sealed record CheckpointRequest(DateTimeOffset? At = null);

    public sealed record PlayerCountRequest(int PlayerCount);

    [HttpPost]
    public async Task<IActionResult> Start(StartRequest body, CancellationToken ct)
    {
        if (!Enum.TryParse<GameClub.Billing.PlayMode>(body.Mode, true, out var parsed))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_mode", $"Unknown play mode '{body.Mode}'.");
        }

        var session = await sessions.StartAsync(body.StationId, body.PlayerCount, parsed, body.StartedAt, ct);
        return Created($"/api/sessions/{session.Id}", new { session.Id, session.StationId, session.Status });
    }

    [HttpPost("{id:guid}/pause")]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct)
    {
        await sessions.PauseAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/resume")]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct)
    {
        await sessions.ResumeAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/stop")]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        var invoice = await sessions.StopAsync(id, ct);
        return Ok(new
        {
            invoice.Number,
            unroundedPayableToman = invoice.UnroundedPayableToman,
            roundingToman = invoice.RoundingToman,
            collectibleToman = invoice.CollectibleToman,
        });
    }

    public sealed record ManualSettleRequest(DateTimeOffset? EndAt = null);

    [HttpPost("{id:guid}/manual-settle")]
    public async Task<IActionResult> ManualSettle(Guid id, ManualSettleRequest body, CancellationToken ct)
    {
        var invoice = await sessions.ResolveManualSettleAsync(id, body.EndAt, ct);
        return Ok(new
        {
            invoice.Number,
            unroundedPayableToman = invoice.UnroundedPayableToman,
            roundingToman = invoice.RoundingToman,
            collectibleToman = invoice.CollectibleToman,
        });
    }

    [HttpGet("{id:guid}/live-amount")]
    public async Task<IActionResult> LiveAmount(Guid id, CancellationToken ct)
    {
        var amount = await sessions.LiveAmountAsync(id, ct);
        return Ok(new { amountToman = amount });
    }

    [HttpPost("{id:guid}/checkpoints")]
    public async Task<IActionResult> Checkpoint(Guid id, CheckpointRequest body, CancellationToken ct)
    {
        await sessions.RecordCheckpointAsync(id, body.At, ct);
        return NoContent();
    }

    [HttpPatch("{id:guid}/player-count")]
    public async Task<IActionResult> PlayerCount(Guid id, PlayerCountRequest body, CancellationToken ct)
    {
        await sessions.ChangePlayerCountAsync(id, body.PlayerCount, ct);
        return NoContent();
    }

    [HttpPost("~/api/internal/dead-pc-sweep")]
    public async Task<IActionResult> DeadPcSweep(CancellationToken ct)
    {
        var count = await deadPc.RunDueClosuresAsync(ct);
        return Ok(new { closed = count });
    }
}

[ApiController]
[Route("api/audit")]
public sealed class AuditController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.AuditLog.OrderBy(a => a.Id).Select(a => new { a.Id, a.Action }).ToListAsync(ct);
        return Ok(rows);
    }
}
