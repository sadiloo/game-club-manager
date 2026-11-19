using GameClub.Server.Time;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/local")]
public sealed class LocalTestClockController : ControllerBase
{
    [HttpPost("advance-clock")]
    public IActionResult Advance([FromBody] AdvanceClockRequest body, [FromServices] IClock clock)
    {
        if (!LocalOpenSessionsController.IsLoopbackAddress(HttpContext.Connection.RemoteIpAddress, allowAnonymousNull: false))
        {
            return NotFound();
        }

        if (!HasValidApiKey())
        {
            return NotFound();
        }

        if (clock is not MutableClock mutable)
        {
            return NotFound();
        }

        if (body.Seconds <= 0 || body.Seconds > 86_400)
        {
            return BadRequest();
        }

        mutable.Advance(TimeSpan.FromSeconds(body.Seconds));
        return Ok(new { utcNow = mutable.UtcNow });
    }

    private bool HasValidApiKey()
    {
        var expected = HttpContext.RequestServices.GetRequiredService<IConfiguration>()["GAMECLUB_API_KEY"]
            ?? Environment.GetEnvironmentVariable("GAMECLUB_API_KEY");
        if (string.IsNullOrWhiteSpace(expected))
        {
            return false;
        }

        return string.Equals(
            Request.Headers["X-Api-Key"].ToString(),
            expected,
            StringComparison.Ordinal);
    }

    public sealed record AdvanceClockRequest(int Seconds);
}