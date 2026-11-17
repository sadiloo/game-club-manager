using GameClub.Billing;
using GameClub.Server;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Mvc;

namespace GameClub.Server.Controllers;

[ApiController]
[Route("api/operator/game-titles")]
public sealed class OperatorGameTariffController(GameTariffService gameTariff, OperatorContext context) : ControllerBase
{
    private void RequireManager()
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Only a manager may edit game rates.");
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        RequireManager();
        return Ok(await gameTariff.ListTitlesForManagerAsync(ct));
    }

    public sealed record SaveGameTitleBody(
        string Name,
        bool Enabled,
        IReadOnlyList<RateRowBody> Rates);

    public sealed record RateRowBody(string Mode, int? PlayerCount, long HourlyToman, long? NightHourlyToman);

    [HttpPost]
    public async Task<IActionResult> Create(SaveGameTitleBody body, CancellationToken ct)
    {
        RequireManager();
        var rates = MapRates(body.Rates);
        var saved = await gameTariff.SaveTitleAsync(null, body.Name, body.Enabled, rates, ct);
        return Created($"/api/operator/game-titles/{saved.Id}", new { saved.Id, saved.Name, saved.Enabled });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, SaveGameTitleBody body, CancellationToken ct)
    {
        RequireManager();
        var rates = MapRates(body.Rates);
        var saved = await gameTariff.SaveTitleAsync(id, body.Name, body.Enabled, rates, ct);
        return Ok(new { saved.Id, saved.Name, saved.Enabled });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        RequireManager();
        await gameTariff.DeleteTitleIfUnusedAsync(id, ct);
        return NoContent();
    }

    private static IReadOnlyList<GameRateLineInput> MapRates(IReadOnlyList<RateRowBody> rows)
        => rows.Select(r => new GameRateLineInput(
            Enum.Parse<PlayMode>(r.Mode, true),
            r.PlayerCount,
            r.HourlyToman,
            r.NightHourlyToman)).ToList();
}