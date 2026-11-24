using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/local")]
public sealed class LocalOpenSessionsController(AppDbContext db, IConfiguration config) : ControllerBase
{
    [HttpGet("open-sessions")]
    public async Task<IActionResult> GetOpenSessions(CancellationToken ct)
    {
        if (!IsLoopback())
        {
            return NotFound();
        }

        if (!HasValidApiKey())
        {
            return NotFound();
        }

        var count = await db.Sessions.AsNoTracking()
            .CountAsync(s => s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused, ct);
        return Ok(new { count });
    }

    private bool IsLoopback()
    {
        var env = HttpContext.RequestServices.GetService<IWebHostEnvironment>();
        var allowNull = env?.IsDevelopment() == true;
        return IsLoopbackAddress(HttpContext.Connection.RemoteIpAddress, allowNull);
    }

    internal static bool IsLoopbackAddress(System.Net.IPAddress? remote, bool allowAnonymousNull = false)
    {
        if (remote is null)
        {
            return allowAnonymousNull;
        }

        if (System.Net.IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            return System.Net.IPAddress.IsLoopback(remote.MapToIPv4());
        }

        return false;
    }

    private bool HasValidApiKey()
    {
        var expected = config["GAMECLUB_API_KEY"] ?? config["ApiKey"] ?? "dev-key";
        return Request.Headers.TryGetValue(ApiKeyMiddleware.HeaderName, out var provided)
               && string.Equals(provided.ToString(), expected, StringComparison.Ordinal);
    }
}