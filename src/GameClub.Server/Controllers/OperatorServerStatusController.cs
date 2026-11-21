using GameClub.Server.Operator;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator")]
public sealed class OperatorServerStatusController(ServerStatusService status) : ControllerBase
{
    [HttpGet("server-status")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var dto = await status.ReadAsync(ct);
        return Ok(new
        {
            startedAtUtc = dto.StartedAtUtc,
            version = dto.Version,
            lastBackupUtc = dto.LastBackupUtc,
            dbOk = dto.DbOk,
        });
    }
}