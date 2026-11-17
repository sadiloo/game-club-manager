using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1RateInputTests(GameClubWebApplicationFactory factory)
{
    private static readonly object[] ValidOnlyPlayerOne =
    [
        new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L },
    ];

    [Theory]
    [InlineData("\"120.000\"")]
    [InlineData("\"1.5\"")]
    [InlineData("\"1e5\"")]
    [InlineData("\"0x1F\"")]
    [InlineData("\"12 000\"")]
    public async Task FixR1_R2_invalid_rate_string_returns_400_persian_nothing_saved(string hourlyJson)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var json = $$"""
            {
              "name": "BadRate",
              "enabled": true,
              "rates": [
                { "mode": "Offline", "playerCount": 1, "hourlyToman": {{hourlyJson}}, "nightHourlyToman": 180000 }
              ]
            }
            """;
        var res = await mgr.PostAsync(
            "/api/operator/game-titles",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("نرخ", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.GameTitles.AnyAsync());
    }

    [Fact]
    public async Task FixR1_R2_json_fractional_number_returns_400_persian()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var res = await mgr.PostAsync(
            "/api/operator/game-titles",
            new StringContent(
                """{"name":"Frac","enabled":true,"rates":[{"mode":"Offline","playerCount":1,"hourlyToman":1.5}]}""",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain("JSON", body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("نرخ", body.GetProperty("message").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixR1_R2_missing_rate_message_names_game_and_option()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-MR", 2);
        var create = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new { name = "FC27", enabled = true, rates = ValidOnlyPlayerOne });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-MR/start", new { gameTitleId = gameId });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        var json = await start.Content.ReadFromJsonAsync<JsonElement>();
        var msg = json.GetProperty("message").GetString()!;
        Assert.Contains("FC27", msg, StringComparison.Ordinal);
        Assert.Contains("آفلاین", msg, StringComparison.Ordinal);
        Assert.Contains("دسته", msg, StringComparison.Ordinal);
        Assert.Contains("نرخ", msg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixR1_R2_disabled_game_message_names_game_and_option()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-DG", 1);
        var create = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        create.EnsureSuccessStatusCode();
        var gameId = (await create.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetInt32();
        await mgr.PutAsJsonAsync(
            $"/api/operator/game-titles/{gameId}",
            new
            {
                name = "FC27",
                enabled = false,
                rates = new[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L } },
            });
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-DG/start", new { gameTitleId = gameId });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
        var msg = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString()!;
        Assert.Contains("FC27", msg, StringComparison.Ordinal);
        Assert.Contains("گزینه", msg, StringComparison.Ordinal);
        Assert.Contains("آفلاین", msg, StringComparison.Ordinal);
        Assert.DoesNotContain("Game is disabled", msg, StringComparison.Ordinal);
    }
}