using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Operator;

/// <summary>Test-only hooks; disabled unless <c>GameClub:EnableTestHooks</c> is true.</summary>
[ApiController]
[Route("api/operator/_test")]
public sealed class OperatorTestHooksController(IConfiguration config) : ControllerBase
{
    [HttpGet("unhandled")]
    public IActionResult Unhandled()
    {
        if (!config.GetValue("GameClub:EnableTestHooks", false))
        {
            return NotFound();
        }

        throw new InvalidOperationException("internal diagnostic fault: must not appear in API response");
    }
}