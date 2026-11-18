using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetBillOracleTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task PcInternet_Oracle_E1_three_segments_110000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-O1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 17, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-O1");
        factory.Clock.SetTehran(2026, 6, 2, 17, 45);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, false);
        factory.Clock.SetTehran(2026, 6, 2, 18, 15);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await ToggleAsync(op, sessionId, true);
        factory.Clock.SetTehran(2026, 6, 2, 18, 30);
        await S3TestHelpers.CheckpointAsync(api, sessionId, factory.Clock.UtcNow);
        await api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(110_000, bill.GetProperty("timeGrossToman").GetInt64());
        var segments = bill.GetProperty("segments");
        Assert.Equal(3, segments.GetArrayLength());
    }

    private async Task ToggleAsync(HttpClient op, Guid sessionId, bool on)
    {
        using var scope = factory.Services.CreateScope();
        var rv = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on,
            expectedVersion = rv,
            commandId = Guid.NewGuid(),
        })).EnsureSuccessStatusCode();
    }
}