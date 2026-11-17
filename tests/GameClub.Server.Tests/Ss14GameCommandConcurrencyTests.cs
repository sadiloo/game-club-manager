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

    private static async Task<int> SeedFc27Async(HttpClient mgr, string name = "FC27")
    {
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name,
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

    [Fact]
    public async Task Ss14GameCommand_StaleVersion_409_NoSegmentChange()
    {
        var (sessionId, gameId, ver) = await SeedPlayingWithGameAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var segsBefore = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        var op = await factory.CreateOperatorClientAsync();
        var response = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = (int?)null, expectedVersion = ver + 99, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("stale_version", body, StringComparison.Ordinal);
        segsBefore = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        Assert.Equal(2, segsBefore);
        Assert.Equal(gameId, await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.CurrentGameTitleId).SingleAsync());
    }

    [Fact]
    public async Task Ss14GameCommand_DuplicateCommandId_Idempotent_NoExtraSegment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-ID", 1);
        var gameId = await SeedFc27Async(mgr);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-ID", 1);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        factory.Clock.SetTehran(2026, 8, 8, 14, 15);
        var cmd = Guid.NewGuid();
        var body = new { gameTitleId = gameId, expectedVersion = ver, commandId = cmd };
        var first = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/game", body);
        first.EnsureSuccessStatusCode();
        var segCount = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var second = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = cmd });
        second.EnsureSuccessStatusCode();
        var segAfter = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        Assert.Equal(segCount, segAfter);
    }

    [Fact]
    public async Task Ss14A6_DisabledGame_BlocksNewStart_KeepsRunningSnapshot()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-A6", 1);
        await S3TestHelpers.RegisterConsole(api, op, "PS5-A6B", 1);
        var gameId = await SeedFc27Async(mgr);
        var pesId = await SeedFc27Async(mgr, "PES");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-A6/start", new { gameTitleId = gameId });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await mgr.PutAsJsonAsync(
            $"/api/operator/game-titles/{gameId}",
            new
            {
                name = "FC27",
                enabled = false,
                rates = new object[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L } },
            });
        var blocked = await op.PostAsJsonAsync("/api/operator/stations/PS5-A6B/start", new { gameTitleId = gameId });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var changeOther = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = pesId, expectedVersion = ver, commandId = Guid.NewGuid() });
        changeOther.EnsureSuccessStatusCode();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        Assert.Equal(pesId, session.CurrentGameTitleId);
        var open = session.Segments.Single(s => s.EndUtc is null);
        Assert.Equal(150_000L, open.GameHourlyRateToman);
    }

    [Fact]
    public async Task Ss14GameRate_ZeroHourly_RejectedOnSave()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var response = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new
            {
                name = "BadRate",
                enabled = true,
                rates = new object[] { new { mode = "Offline", playerCount = (int?)1, hourlyToman = 0L, nightHourlyToman = (long?)null } },
            });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ss14GameCommand_BillOpen_409_NoSideEffects()
    {
        var (sessionId, gameId, ver) = await SeedPlayingWithGameAsync();
        var op = await factory.CreateOperatorClientAsync();
        factory.Clock.SetTehran(2026, 8, 8, 14, 25);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var segs = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        ver = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var response = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = (int?)null, expectedVersion = ver, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var after = await db.Sessions.Include(s => s.Segments).Where(s => s.Id == sessionId)
            .Select(s => s.Segments.Count).SingleAsync();
        Assert.Equal(segs, after);
        Assert.Equal(gameId, await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.CurrentGameTitleId).SingleAsync());
    }
}