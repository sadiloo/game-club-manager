using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetToggleApiTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        res.EnsureSuccessStatusCode();
    }

    private static async Task<uint> RowVersionAsync(GameClubWebApplicationFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    [Fact]
    public async Task PcInternetToggle_ConsoleSession_returns_not_pc()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-I1", 1);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-I1/start", new { });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var rv = await RowVersionAsync(factory, sessionId);
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = rv,
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("not_pc", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternetGrid_before_offline_rates_toggle_not_allowed()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-I2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-I2");
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-I2");
        Assert.False(tile.GetProperty("offlineRateAvailable").GetBoolean());
        Assert.False(tile.GetProperty("internetToggleAllowed").GetBoolean());
    }

    [Fact]
    public async Task PcInternetToggle_WithOfflineRates_creates_segment()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-I3", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-I3");
        var rv = await RowVersionAsync(factory, sessionId);
        var cmd = Guid.NewGuid();
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = rv,
            commandId = cmd,
        });
        res.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var modes = await db.Segments.Where(s => s.SessionId == sessionId).OrderBy(s => s.StartUtc)
            .Select(s => s.Mode).ToListAsync();
        Assert.Equal(2, modes.Count);
        Assert.Equal(PlayMode.Online, modes[0]);
        Assert.Equal(PlayMode.Offline, modes[1]);
        var again = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = rv,
            commandId = cmd,
        });
        again.EnsureSuccessStatusCode();
        var body = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("internetOn").GetBoolean());
    }
}