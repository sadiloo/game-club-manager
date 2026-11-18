using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcRatesApiTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task PcRates_Operator_gets_403()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.GetAsync("/api/operator/pc-rates");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("نرخ‌های PC", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PcRates_Manager_upsert_offline_pair_validation()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var onlyDay = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 50_000L, offlineNight = (long?)null });
        Assert.Equal(HttpStatusCode.BadRequest, onlyDay.StatusCode);
        Assert.Equal("offline_pair_required", (await onlyDay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var ok = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        ok.EnsureSuccessStatusCode();
    }
}