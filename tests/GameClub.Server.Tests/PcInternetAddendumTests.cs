using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetAddendumTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task PcInternetGrid_nextBand_day_at_noon()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-NB", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-NB");
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-NB");
        Assert.Equal("day", tile.GetProperty("nextBand").GetString());
        Assert.Equal(60_000, tile.GetProperty("nextHourlyToman").GetInt64());
    }

    [Fact]
    public async Task PcInternetGrid_nextBand_night_in_special_hours()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-NB2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 2, 0);
        await S3TestHelpers.StartSession(api, "PC-NB2");
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-NB2");
        Assert.Equal("night", tile.GetProperty("nextBand").GetString());
        Assert.Equal(80_000, tile.GetProperty("nextHourlyToman").GetInt64());
    }

    [Fact]
    public async Task PcInternetToggle_response_includes_nextBand()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-NB3", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-NB3");
        var rv = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions
            .AsNoTracking().Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = rv,
            commandId = Guid.NewGuid(),
        });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("day", body.GetProperty("nextBand").GetString());
    }

    [Fact]
    public async Task PcInternet_Heartbeat_success_clears_internetError()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-HE", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-HE");
        var op = await factory.CreateOperatorClientAsync();
        var codeRes = await op.PostAsync("/api/operator/stations/PC-HE/pairing-code", null);
        codeRes.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeRes.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var pairRes = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pairRes.EnsureSuccessStatusCode();
        var deviceId = JsonDocument.Parse(await pairRes.Content.ReadAsStringAsync()).RootElement.GetProperty("deviceId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-HE");
        row.DesiredVersion = 2;
        row.AppliedVersion = 1;
        row.LastError = "firewall_not_configured";
        await db.SaveChangesAsync();

        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        await coordinator.OnHeartbeatAsync(deviceId, null, 0, new KioskInternetAppliedReport
        {
            Epoch = row.Epoch,
            Version = 2,
            On = false,
            AppliedAtUtc = factory.Clock.UtcNow.UtcDateTime,
            Error = null,
        }, CancellationToken.None);

        row = await db.StationInternetStates.AsNoTracking().SingleAsync(s => s.StationId == "PC-HE");
        Assert.Null(row.LastError);
        Assert.Equal(2, row.AppliedVersion);
    }

    [Fact]
    public async Task PcRates_Operator_gets_403_persian_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.GetAsync("/api/operator/pc-rates");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
        Assert.Contains("نرخ‌های PC", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }
}