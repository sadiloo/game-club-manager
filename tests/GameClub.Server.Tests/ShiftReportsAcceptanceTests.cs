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
public sealed class ShiftReportsAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private async Task<HttpClient> ManagerAsync() => await factory.CreateOperatorClientAsync("manager", "1234");

    private async Task<HttpClient> OpAsync() => await factory.CreateOperatorClientAsync();

    private static async Task BillConsole110kAsync(
        GameClubWebApplicationFactory factory,
        HttpClient api,
        HttpClient op,
        Guid sessionId)
    {
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await api.PostAsync($"/api/sessions/{sessionId}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await api.PostAsync($"/api/sessions/{sessionId}/resume", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
    }

    [Fact]
    public async Task sr_scenario_01_shift_gate_blocks_money_until_open_shift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-S1", playerCount = 1 })).StatusCode);
        await S3TestHelpers.OpenShiftAsync(op, 500_000);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S1", 1);
        await BillConsole110kAsync(factory, _api, op, sessionId);
        await manager.PutAsJsonAsync("/api/operator/shift/settings", new { shiftsEnabled = false });
        _ = await S3TestHelpers.StartSession(_api, "PS5-S1", 1);
    }

    [Fact]
    public async Task sr_scenario_02_working_day_stamp_on_shift_close()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S2", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S2", 1);
        await BillConsole110kAsync(factory, _api, op, sessionId);
        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 110_000L,
            actualCardReaderToman = 0L,
            discrepancyExplanation = (string?)null,
        });
        close.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices
            .Include(i => i.PaymentLines)
            .SingleAsync();
        Assert.Equal(2026, invoice.WorkingDayYear);
        Assert.Equal(6, invoice.WorkingDayMonth);
        Assert.Equal(3, invoice.WorkingDayDay);
        Assert.NotEmpty(invoice.PaymentLines);
    }

    [Fact]
    public async Task sr_scenario_05_expected_cash_discrepancy_explanation_close_succeeds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterPc(_api, "PC-S5", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.OpenShiftAsync(op, 500_000);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-S5", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        await S3TestHelpers.FinishPcPaymentAsync(factory, sessionId, 80_000);
        var customer = await op.PostAsJsonAsync("/api/operator/customers", new { name = "a", mobile = "09123334444" });
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 20_000L });
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(600_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        var close = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 595_000L,
            actualCardReaderToman = 0L,
            discrepancyExplanation = "کمبود خرد",
        });
        close.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CloseRecords.SingleAsync();
        Assert.Equal(595_000, record.ActualCashToman);
    }

    [Fact]
    public async Task sr_scenario_04_split_methods_in_close_preview()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S4", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S4", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await _api.PostAsync($"/api/sessions/{sessionId}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await _api.PostAsync($"/api/sessions/{sessionId}/resume", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[]
            {
                new { method = "Cash", amountToman = 60_000L },
                new { method = "CardReader", amountToman = 50_000L },
            },
        })).EnsureSuccessStatusCode();
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(60_000, preview.GetProperty("expected").GetProperty("cashExpected").GetInt64());
        Assert.Equal(50_000, preview.GetProperty("expected").GetProperty("cardReaderExpected").GetInt64());
    }

    [Fact]
    public async Task sr_scenario_06_pending_c2c_confirmed_adds_to_original_day_not_confirming_shift()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S6", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "کارت الف" });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S6", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await _api.PostAsync($"/api/sessions/{sessionId}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await _api.PostAsync($"/api/sessions/{sessionId}/resume", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "CardToCard", amountToman = 110_000L, destinationCardId = "card-a", reference = "1" } },
        })).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        })).EnsureSuccessStatusCode();
        var incomeBefore = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(0, incomeBefore.GetProperty("receivedCardToCardToman").GetInt64());
        long lineId;
        using (var scope = factory.Services.CreateScope())
        {
            lineId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentLines
                .Where(p => p.Method == GameClub.Billing.PaymentMethod.CardToCard)
                .Select(p => p.Id)
                .SingleAsync();
        }

        await S3TestHelpers.OpenShiftAsync(op, 0);
        await op.PostAsync($"/api/operator/payments/{lineId}/confirm", null);
        var incomeAfter = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(110_000, incomeAfter.GetProperty("receivedCardToCardToman").GetInt64());
    }

    [Fact]
    public async Task sr_scenario_08_manager_void_after_close_keeps_entered_actual()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S8", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S8", 1);
        await BillConsole110kAsync(factory, _api, op, sessionId);
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 108_000L,
            actualCardReaderToman = 0L,
            discrepancyExplanation = "diff",
        });
        long number;
        using (var scope = factory.Services.CreateScope())
        {
            number = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.Select(i => i.Number).SingleAsync();
        }

        await manager.PostAsJsonAsync($"/api/operator/invoices/{number}/void", new { reason = "اشتباه" });
        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(0, income.GetProperty("receivedCashToman").GetInt64());
        using var scope2 = factory.Services.CreateScope();
        Assert.Equal(108_000, (await scope2.ServiceProvider.GetRequiredService<AppDbContext>().CloseRecords.SingleAsync()).ActualCashToman);
    }

    [Fact]
    public async Task sr_scenario_14_cu8_reports_without_profit_figure()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var body = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/wallet/cu8");
        Assert.True(body.TryGetProperty("realMoneyLiabilityToman", out _));
        Assert.False(body.TryGetProperty("profitToman", out _));
    }

    [Fact]
    public async Task sr_scenario_03_carried_session_stays_open_until_next_settle()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-A", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-B", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-A", 1);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-B", 1);
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
            sessionActions = new[]
            {
                new { sessionId = s1, action = "carry" },
                new { sessionId = s2, action = "carry" },
            },
        })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync(s => s.Id == s1)).Status);
    }

    [Fact]
    public async Task sr_scenario_07_destination_cards_in_close_preview()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "A" });
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.True(preview.TryGetProperty("destinationCards", out _));
    }

    [Fact]
    public async Task sr_scenario_09_wallet_play_does_not_double_received_money()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, false);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S9", 1);
        var customer = (await op.PostAsJsonAsync("/api/operator/customers", new { name = "w", mobile = "09125556677" })).EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up", new { method = "Cash", amountToman = 100_000L });
        await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 100_000L,
            actualCardReaderToman = 0L,
        });
        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(100_000, income.GetProperty("receivedMoneyToman").GetInt64());
    }

    [Fact]
    public async Task sr_scenario_12_history_filter_by_cash_method()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S12", 1);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-S12", 1);
        await BillConsole110kAsync(factory, _api, op, sessionId);
        var history = await op.GetFromJsonAsync<JsonElement>("/api/operator/reports/history?method=Cash");
        Assert.True(history.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task sr_scenario_13_usage_report_has_no_utilization_percent()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var from = S3TestHelpers.T(2026, 6, 1, 0, 0).UtcDateTime;
        var to = S3TestHelpers.T(2026, 6, 7, 23, 59).UtcDateTime;
        var usage = await manager.GetFromJsonAsync<JsonElement>($"/api/operator/reports/usage?fromUtc={from:o}&toUtc={to:o}");
        Assert.True(usage.TryGetProperty("byStation", out _));
        Assert.False(usage.TryGetProperty("utilizationPercent", out _));
    }

    [Fact]
    public async Task sr_scenario_15_close_succeeds_without_sms()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await ManagerAsync();
        var op = await OpAsync();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var response = await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 0L,
            actualCardReaderToman = 0L,
        });
        response.EnsureSuccessStatusCode();
    }
}
