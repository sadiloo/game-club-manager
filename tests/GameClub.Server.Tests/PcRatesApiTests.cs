using System.Net;
using System.Net.Http.Json;
using System.Text;
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

    [Fact]
    public async Task PcRates_Manager_put_empty_body_returns_400_persian()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("عدد معتبر", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PcRates_Manager_put_response_returns_saved_values()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 85_000L, onlineNight = 105_000L, offlineDay = 55_000L, offlineNight = 75_000L });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(85_000, body.GetProperty("onlineDay").GetInt64());
        Assert.Equal(105_000, body.GetProperty("onlineNight").GetInt64());
        Assert.Equal(55_000, body.GetProperty("offlineDay").GetInt64());
        Assert.Equal(75_000, body.GetProperty("offlineNight").GetInt64());
    }
}