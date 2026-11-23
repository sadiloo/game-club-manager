using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/settings")]
public sealed class OperatorRoundingSettingsController(RoundingSettingsService rounding) : ControllerBase
{
    [HttpGet("rounding")]
    public async Task<IActionResult> Get(CancellationToken ct) => Ok(await rounding.GetAsync(ct));

    [HttpPut("rounding")]
    public async Task<IActionResult> Put(RoundingSettingsUpdateRequest body, CancellationToken ct)
        => Ok(await rounding.UpdateAsync(body, ct));
}