using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14GameTariffIntegrationTests(GameClubWebApplicationFactory factory)
{
    private async Task<int> SeedFc27Async(HttpClient mgr)
    {
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27",
                enabled = true,
                rates = new object[]
                {
                    new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L },
                    new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L, nightHourlyToman = 200_000L },
                    new { mode = "Offline", playerCount = (int?)3, hourlyToman = 200_000L, nightHourlyToman = 230_000L },
                    new { mode = "Offline", playerCount = (int?)4, hourlyToman = 260_000L, nightHourlyToman = 290_000L },
                    new { mode = "Online", playerCount = (int?)null, hourlyToman = 190_000L, nightHourlyToman = 220_000L },
                },
            });
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        return Convert.ToInt32(json!["id"]);
    }

    [Fact]
    public async Task Ss14Example01_MidSessionGameChange_130000_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-1", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        var cmd = Guid.NewGuid();
        var change = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = session.RowVersion, commandId = cmd });
        change.EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 15, 0);
        var stop = await factory.CreateClientWithKey().PostAsJsonAsync($"/api/sessions/{sessionId}/stop", new { });
        stop.EnsureSuccessStatusCode();
        session = await db.Sessions.Include(s => s.Invoice).SingleAsync(s => s.Id == sessionId);
        Assert.NotNull(session.Invoice);
        Assert.Equal(130_000, session.Invoice!.CollectibleToman);
    }

    [Fact]
    public async Task Ss14Q14_OperatorGets403_ManagerSucceedsWithAudit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var denied = await op.GetAsync("/api/operator/game-titles");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var ok = await mgr.GetAsync("/api/operator/game-titles");
        ok.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Ss14R1_TransferConsoleToConsole_ClearsGame()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-2", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-1", 1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = session.RowVersion, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Null(session.CurrentGameTitleId);
        var open = session.Segments.OrderBy(s => s.StartUtc).Last();
        Assert.Null(open.GameTitleId);
        Assert.Equal("انتقال", open.SplitReason);
    }
}