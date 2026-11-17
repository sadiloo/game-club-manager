using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1eModeTests(GameClubWebApplicationFactory factory)
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Offline,Online")]
    public async Task FixR1e_mode_accepts_defined_names_only_returns_400_invalid_mode(string mode)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "BadMode",
                enabled = true,
                rates = new[] { new { mode, playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_mode", json.GetProperty("code").GetString());
        Assert.Contains("حالت بازی", json.GetProperty("message").GetString()!, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GameTitles.AnyAsync());
    }
}