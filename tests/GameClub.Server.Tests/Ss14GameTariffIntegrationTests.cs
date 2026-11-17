using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
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
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetInt32();
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
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 130_000, 130_000);
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

    private async Task<long> StopAndCollectibleAsync(Guid sessionId)
    {
        var stop = await factory.CreateClientWithKey().PostAsJsonAsync($"/api/sessions/{sessionId}/stop", new { });
        stop.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.SessionId == sessionId);
        return invoice.CollectibleToman;
    }

    private async Task<uint> SessionVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId)).RowVersion;
    }

    [Fact]
    public async Task Ss14Example06_RemoveGame_125000_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-1", 1);
        var ver0 = await SessionVersionAsync(sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver0, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        var ver = await SessionVersionAsync(sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = (int?)null, expectedVersion = ver, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 8, 8, 15, 0);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 123_333, 125_000);
    }

    [Fact]
    public async Task Ss14PcStartWithGame_400_NoSession()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-G", PilotSeed.PcNormalGroupId);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var gameId = await SeedFc27Async(mgr);
        var response = await op.PostAsJsonAsync("/api/operator/stations/PC-G/start", new { gameTitleId = gameId });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Sessions.AnyAsync());
    }

    [Fact]
    public async Task Ss14MissingRate_ChangeGame_409_NoSideEffects()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 4);
        var partial = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "FC27-PART",
                enabled = true,
                rates = new object[] { new { mode = "Offline", playerCount = (int?)4, hourlyToman = 260_000L } },
            });
        partial.EnsureSuccessStatusCode();
        var gameId = (await partial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        factory.Clock.SetTehran(2026, 8, 8, 20, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-1/start", new { gameTitleId = gameId });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        factory.Clock.SetTehran(2026, 8, 8, 20, 40);
        var beforeSegs = session.Segments.Count;
        var response = await op.PatchAsJsonAsync(
            $"/api/operator/sessions/{session.Id}/player-count",
            new { playerCount = 2 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        session = await db.Sessions.Include(s => s.Segments).AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(4, session.PlayerCount);
        Assert.Equal(beforeSegs, session.Segments.Count);
    }

    [Fact]
    public async Task Ss14Example09_PrepaidRecalcAfterGame_46Min()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-1", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L });
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        var ver = await SessionVersionAsync(sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() });
        using var scope = factory.Services.CreateScope();
        var booked = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds).SingleAsync();
        Assert.Equal(46 * 60L, booked);
    }

    [Fact]
    public async Task Ss14C6_BillCancel_GameUnchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-1", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-1", 1);
        var ver = await SessionVersionAsync(sessionId);
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() });
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/cancel", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(gameId, session.CurrentGameTitleId);
        var open = session.Segments.Single(s => s.EndUtc is null);
        Assert.Equal(gameId, open.GameTitleId);
        Assert.Equal(150_000L, open.GameHourlyRateToman);
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