using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcInternetGridTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task PcInternetGrid_PlayingPc_includes_section_7_1_fields()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L });
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-G1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-G1");
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-G1");
        Assert.True(tile.TryGetProperty("internetOn", out var on));
        Assert.True(on.ValueKind == JsonValueKind.True);
        Assert.True(tile.GetProperty("internetDesiredOn").GetBoolean());
        Assert.True(tile.TryGetProperty("internetPending", out _));
        Assert.True(tile.TryGetProperty("kioskConnected", out _));
        Assert.True(tile.TryGetProperty("internetToggleAllowed", out _));
        Assert.Equal(60_000, tile.GetProperty("nextHourlyToman").GetInt64());
    }

    [Fact]
    public async Task PcInternetGrid_Console_has_no_internet_toggle()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-G1", 1);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await op.PostAsJsonAsync("/api/operator/stations/PS5-G1/start", new { });
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PS5-G1");
        Assert.False(tile.TryGetProperty("internetToggleAllowed", out var toggle) && toggle.ValueKind == JsonValueKind.True);
    }
}