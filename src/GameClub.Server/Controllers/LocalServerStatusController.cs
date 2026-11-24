using GameClub.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using GameClub.Server.Data;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/local")]
public sealed class LocalServerStatusController(AppDbContext db, ServerRuntimeStatus runtime) : ControllerBase
{
    [HttpGet("server-status")]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var env = HttpContext.RequestServices.GetService<IWebHostEnvironment>();
        var allowNull = env?.IsDevelopment() == true;
        if (!LocalOpenSessionsController.IsLoopbackAddress(HttpContext.Connection.RemoteIpAddress, allowNull))
        {
            return NotFound();
        }

        var dbOk = await ProbeDatabaseAsync(ct);
        return Ok(new
        {
            startedAtUtc = runtime.ApplicationStartedAtUtc,
            dbOk,
        });
    }

    private async Task<bool> ProbeDatabaseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}