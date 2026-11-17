using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14GameCommandConcurrencyTests(GameClubWebApplicationFactory factory)
{
    private async Task<(Guid SessionId, int GameId, uint Version)> SeedPlayingWithGameAsync()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-C4", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-C4", 1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = session.RowVersion, commandId = Guid.NewGuid() });
        session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        return (sessionId, gameId, session.RowVersion);
    }

    private static async Task<int> SeedFc27Async(HttpClient mgr)
    {
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new object[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L } },
            });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Ss14GameCommand_ConcurrentGameWithBillOpen_OneSessionBusy()
    {
        var sawBusy = false;
        for (var i = 0; i < 20; i++)
        {
            var (sessionId, gameId, _) = await SeedPlayingWithGameAsync();
            factory.Clock.SetTehran(2026, 8, 8, 14, 10);
            var ver = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
                .Sessions.AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
            var opA = await factory.CreateOperatorClientAsync();
            var opB = await factory.CreateOperatorClientAsync();
            var results = await Task.WhenAll(
                opA.PostAsJsonAsync(
                    $"/api/operator/sessions/{sessionId}/game",
                    new { gameTitleId = (int?)null, expectedVersion = ver, commandId = Guid.NewGuid() }),
                opB.PostAsJsonAsync(
                    $"/api/operator/sessions/{sessionId}/game",
                    new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() }));
            var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadAsStringAsync()));
            sawBusy |= bodies.Any(b => b.Contains("session_busy", StringComparison.Ordinal));
            Assert.InRange(results.Count(r => r.IsSuccessStatusCode), 1, 2);
        }

        Assert.True(sawBusy);
    }
}