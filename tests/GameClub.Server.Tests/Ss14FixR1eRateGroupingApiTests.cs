using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1eRateGroupingApiTests(GameClubWebApplicationFactory factory)
{
    [Theory]
    [InlineData("150,00")]
    [InlineData("1,,5")]
    [InlineData(",150000")]
    [InlineData("150000,")]
    [InlineData("1,50,000")]
    [InlineData("000,150")]
    [InlineData("0,150,000")]
    [InlineData("0150000")]
    public async Task FixR1e_invalid_grouped_rate_string_returns_400_persian_nothing_saved(string hourly)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var json = $$"""
            {
              "name": "BadGroup",
              "enabled": true,
              "rates": [
                { "mode": "Offline", "playerCount": 1, "hourlyToman": "{{hourly}}" }
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

    [Theory]
    [InlineData("150000", 150_000L)]
    [InlineData("150,000", 150_000L)]
    [InlineData("1,500,000", 1_500_000L)]
    public async Task FixR1e_valid_grouped_rate_string_saved(string hourly, long expected)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var json = $$"""
            {
              "name": "GroupedOk",
              "enabled": true,
              "rates": [
                { "mode": "Offline", "playerCount": 1, "hourlyToman": "{{hourly}}" }
              ]
            }
            """;
        var res = await mgr.PostAsync(
            "/api/operator/game-titles",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.GameRateLines.SingleAsync();
        Assert.Equal(expected, line.HourlyToman);
    }
}