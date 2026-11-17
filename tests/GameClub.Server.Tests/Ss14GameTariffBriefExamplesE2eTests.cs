using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14GameTariffBriefExamplesE2eTests(GameClubWebApplicationFactory factory)
{
    private static readonly object[] Fc27RateTable =
    [
        new { mode = "Offline", playerCount = (int?)1, hourlyToman = 150_000L, nightHourlyToman = 180_000L },
        new { mode = "Offline", playerCount = (int?)2, hourlyToman = 170_000L, nightHourlyToman = 200_000L },
        new { mode = "Offline", playerCount = (int?)3, hourlyToman = 200_000L, nightHourlyToman = 230_000L },
        new { mode = "Offline", playerCount = (int?)4, hourlyToman = 260_000L, nightHourlyToman = 290_000L },
        new { mode = "Online", playerCount = (int?)null, hourlyToman = 190_000L, nightHourlyToman = 220_000L },
    ];

    private static async Task<int> SeedTitleAsync(HttpClient mgr, string name)
    {
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/game-titles",
            new { name, enabled = true, rates = Fc27RateTable });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<uint> RowVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    private async Task<long> BookedSecondsAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.PrepaidBookedActiveSeconds ?? 0).SingleAsync();
    }

    private async Task<JsonElement> OpenConsoleBillAsync(HttpClient op, Guid sessionId)
    {
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        return await bill.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<int> ChargeLineCountAsync(Guid sessionId, DateTimeOffset frozenAtUtc)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<SessionBilling>();
        var session = await db.Sessions.Include(s => s.Segments).SingleAsync(s => s.Id == sessionId);
        var charge = await billing.BuildChargeAsync(session, frozenAtUtc.UtcDateTime, default);
        return charge.Lines.Count;
    }

    [Fact]
    public async Task Ss14Example02_PlayerCountFourToTwo_260000_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-2", 4);
        var gameId = await SeedTitleAsync(mgr, "FC27");
        factory.Clock.SetTehran(2026, 8, 8, 20, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-2/start", new { gameTitleId = gameId });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 8, 8, 20, 40);
        (await op.PatchAsJsonAsync($"/api/operator/sessions/{sessionId}/player-count", new { playerCount = 2 }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 21, 10);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 258_333, 260_000);
    }

    [Fact]
    public async Task Ss14Example02_OnlineFortyFiveMinutes_145000_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-OL", 1);
        await op.PatchAsJsonAsync("/api/operator/stations/PS5-OL/defaults", new { playerCount = 1, mode = "Online" });
        var gameId = await SeedTitleAsync(mgr, "FC27");
        factory.Clock.SetTehran(2026, 8, 8, 20, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-OL/start", new { gameTitleId = gameId });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 8, 8, 20, 45);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 142_500, 145_000);
    }

    [Fact]
    public async Task Ss14Example03_FractionalHop_129333_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 1);
        var gameId = await SeedTitleAsync(mgr, "FC27");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 30, 30);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 15, 0, 0);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 129_333, 130_000);
    }

    [Fact]
    public async Task Ss14Example07_TwoGamesSameRate_56666_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 2);
        var fcId = await SeedTitleAsync(mgr, "FC27");
        var pesId = await SeedTitleAsync(mgr, "PES");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-1/start", new { gameTitleId = fcId });
        start.EnsureSuccessStatusCode();
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 8, 8, 14, 10);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = pesId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 56_666, 60_000);
        var frozen = bill.GetProperty("frozenAtUtc").GetDateTimeOffset();
        Assert.Equal(2, await ChargeLineCountAsync(sessionId, frozen));
    }

    [Fact]
    public async Task Ss14Example08_TransferAndContinuation_145000_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(api, op, "PS5-2", 1);
        var gameId = await SeedTitleAsync(mgr, "FC27");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-1/start", new { gameTitleId = gameId });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        (await S3TestHelpers.TransferAsync(op, sessionId, "PS5-2")).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 40);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 15, 0);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 143_333, 145_000);
    }

    [Fact]
    public async Task Ss14Example10_D4_BudgetExhausted_20Minutes_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 1);
        var gameId = await SeedTitleAsync(mgr, "FC27");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-1", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 37_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 19, 0);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = gameId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        Assert.Equal(20 * 60L, await BookedSecondsAsync(sessionId));
    }

    [Fact]
    public async Task Ss14Example11_D6_MidSessionPrepaid_51Min_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        (await op.PatchAsJsonAsync($"/api/operator/sessions/{sessionId}/player-count", new { playerCount = 2 }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 30);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        Assert.Equal(51 * 60L, await BookedSecondsAsync(sessionId));
    }

    [Fact]
    public async Task Ss14Example12_SubMinuteGame_NoPesLine_56667_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("owner", "5678");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 2);
        var fcId = await SeedTitleAsync(mgr, "FC27");
        var pesId = await SeedTitleAsync(mgr, "PES");
        factory.Clock.SetTehran(2026, 8, 8, 14, 0, 0);
        var start = await op.PostAsJsonAsync("/api/operator/stations/PS5-1/start", new { gameTitleId = fcId });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 8, 8, 14, 10, 10);
        var ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = pesId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 10, 30);
        ver = await RowVersionAsync(sessionId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/game",
            new { gameTitleId = fcId, expectedVersion = ver, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 20, 0);
        var bill = await OpenConsoleBillAsync(op, sessionId);
        S3TestHelpers.AssertMoney(bill, 56_667, 60_000);
        var frozen = bill.GetProperty("frozenAtUtc").GetDateTimeOffset();
        Assert.Equal(2, await ChargeLineCountAsync(sessionId, frozen));
    }

    [Fact]
    public async Task Ss14Example13_PrepaidPlayerCount_51Min_e2e()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-1", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/amount", new { amountToman = 100_000L }))
            .EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 8, 8, 14, 20);
        (await op.PatchAsJsonAsync($"/api/operator/sessions/{sessionId}/player-count", new { playerCount = 2 }))
            .EnsureSuccessStatusCode();
        Assert.Equal(51 * 60L, await BookedSecondsAsync(sessionId));
    }
}