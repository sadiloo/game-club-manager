using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>Brief §10.3 cafe scenarios not covered elsewhere.</summary>
[Collection("server")]
public sealed class PcInternetServerScenariosTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task PcStart_Ignores_ModeOffline_stays_Online()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-S1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var res = await api.PostAsJsonAsync("/api/sessions", new { stationId = "PC-S1", playerCount = 1, mode = "Offline" });
        res.EnsureSuccessStatusCode();
        var sessionId = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mode = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Mode).SingleAsync();
        Assert.Equal(PlayMode.Online, mode);
    }

    [Fact]
    public async Task PcInternetToggle_stale_version_returns_conflict()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterPc(api, "PC-S2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-S2");
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = 0u,
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("stale_version", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternetToggle_manager_and_owner_allowed()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var owner = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterPc(api, "PC-S3A", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, "PC-S3B", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionMgr = await S3TestHelpers.StartSession(api, "PC-S3A");
        var sessionOwner = await S3TestHelpers.StartSession(api, "PC-S3B");
        async Task<uint> Rv(Guid id)
        {
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
                .Where(s => s.Id == id).Select(s => s.RowVersion).SingleAsync();
        }

        (await mgr.PostAsJsonAsync($"/api/operator/sessions/{sessionMgr}/internet", new
        {
            on = false,
            expectedVersion = await Rv(sessionMgr),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
        (await owner.PostAsJsonAsync($"/api/operator/sessions/{sessionOwner}/internet", new
        {
            on = false,
            expectedVersion = await Rv(sessionOwner),
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
    }
}