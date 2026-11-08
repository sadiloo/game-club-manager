using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftReportsFixRound3Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private static string AppJsPath() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "GameClub.Server", "wwwroot", "operator", "app.js"));

    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task BuffetOnlyCashAsync(HttpClient op, long amountToman)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new
        {
            catalogItemId = "custom-service",
            customAmountToman = amountToman,
        });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        (await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new
        {
            payments = new[] { new { method = "Cash", amountToman = amountToman } },
        })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task sr_r3_open_session_close_settle_preview_succeeds_without_prior_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R3-SET", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R3-SET", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new
        {
            catalogItemId = CatalogSeed.CustomServiceId,
            customAmountToman = 20_000L,
        });
        var preview = await op.GetAsync($"/api/operator/sessions/{sessionId}/close-settle-preview");
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(20_000, body.GetProperty("collectibleToman").GetInt64());
        var billGet = await op.GetAsync($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(HttpStatusCode.Conflict, billGet.StatusCode);
    }

    [Fact]
    public async Task sr_r3_close_one_step_settle_on_playing_session_stores_cash_expected_620_actual_595()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R3-620", 1);
        await S3TestHelpers.OpenShiftAsync(op, 500_000);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "c620", mobile = "09126667788" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await BuffetOnlyCashAsync(op, 80_000);
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L });
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R3-620", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new
        {
            catalogItemId = CatalogSeed.CustomServiceId,
            customAmountToman = 20_000L,
        });
        var response = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 595_000L,
            actualCardReaderToman = 0L,
            discrepancyExplanation = "کسری",
            sessionActions = new[] { new { sessionId, action = "settle" } },
        });
        response.EnsureSuccessStatusCode();
        var closeBody = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(620_000, closeBody.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        Assert.Equal(595_000, closeBody.GetProperty("actualCashToman").GetInt64());
        var closeId = closeBody.GetProperty("closeRecordId").GetGuid();
        var income = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{closeId}/income");
        Assert.Equal(620_000, income.GetProperty("expectedCashToman").GetInt64());
        Assert.Equal(595_000, income.GetProperty("actualCashToman").GetInt64());
    }

    [Fact]
    public async Task sr_r3_off_late_wallet_c2c_on_originating_close_expected_not_next()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/qa-card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        factory.Clock.SetTehran(2026, 11, 20, 14, 0);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "wc3", mobile = "09129990002" }))
            .EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new
        {
            method = "CardToCard",
            amountToman = 30_000L,
            destinationCardId = "qa-card-a",
        })).EnsureSuccessStatusCode();
        long pendingId;
        using (var scope = factory.Services.CreateScope())
        {
            pendingId = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .PendingWalletTopUps
                .Select(p => p.Id)
                .SingleAsync();
        }

        var close1 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 20 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        close1.EnsureSuccessStatusCode();
        var close1Id = (await close1.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        factory.Clock.SetTehran(2026, 11, 21, 10, 0);
        await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null);
        var close2 = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 11, day = 21 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        close2.EnsureSuccessStatusCode();
        var close2Id = (await close2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("closeRecordId").GetGuid();

        var inc1 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close1Id}/income");
        var inc2 = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/close/{close2Id}/income");
        Assert.Equal(30_000, inc1.GetProperty("expectedCardToCardToman").GetInt64());
        Assert.Equal(0, inc2.GetProperty("expectedCardToCardToman").GetInt64());
        var day20 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=11&day=20");
        Assert.Equal(30_000, day20.GetProperty("receivedCardToCardToman").GetInt64());
        var day21 = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=11&day=21");
        Assert.Equal(0, day21.GetProperty("receivedCardToCardToman").GetInt64());
    }

    [Fact]
    public async Task sr_r3_history_date_filter_and_export_columns_match_list()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-HIST", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-HIST", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s1}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, s1, 110_000);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 110_000L,
            actualCardReaderToman = 0L,
        });

        factory.Clock.SetTehran(2026, 6, 4, 14, 0);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-HIST", 1);
        factory.Clock.SetTehran(2026, 6, 4, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{s2}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, s2, 110_000);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 4 },
            actualCashToman = 110_000L,
            actualCardReaderToman = 0L,
        });

        var from = S3TestHelpers.T(2026, 6, 3, 0, 0).UtcDateTime;
        var to = S3TestHelpers.T(2026, 6, 3, 23, 59).UtcDateTime;
        var history = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/history?fromUtc={from:o}&toUtc={to:o}");
        Assert.Equal(1, history.GetArrayLength());

        var export = await manager.GetAsync($"/api/operator/reports/history/export?fromUtc={from:o}&toUtc={to:o}");
        export.EnsureSuccessStatusCode();
        var csv = await export.Content.ReadAsStringAsync();
        var header = csv.Split('\n')[0].Trim();
        Assert.Equal(
            "number,status,collectible,stationId,settledBy,workingDayYear,workingDayMonth,workingDayDay,payments,operatorNote,voidReason",
            header);
        Assert.Equal(2, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void sr_r3_pwa_preserves_actual_cash_and_renders_report_columns()
    {
        var js = File.ReadAllText(AppJsPath());
        var html = File.ReadAllText(Path.Combine(Path.GetDirectoryName(AppJsPath())!, "index.html"));
        Assert.Contains("close-settle-preview", js);
        Assert.DoesNotContain("closeCash\").value = closeBaseExpectedCash", js);
        Assert.DoesNotContain("cashEl.value = closeBaseExpectedCash", js);
        Assert.Contains("playToman", js);
        Assert.Contains("peakHours", js);
        Assert.Contains("histFromJy", html);
        Assert.Contains("histToJy", html);
        Assert.Contains("fromUtc", js);
        Assert.Contains("historyTableHeaders", js);
        Assert.Contains("operatorNote", js);
        Assert.Contains("voidReason", js);
        Assert.DoesNotContain("pendInv || pendWal", js);
    }
}
