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
public sealed class S3OperatorPanelAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task s3_scenario_01_grid_playing_and_free_tiles()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await S3TestHelpers.RegisterPc(_api, "PC-FREE", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var ps5 = grid.EnumerateArray().First(e => e.GetProperty("id").GetString() == "PS5-1");
        Assert.Equal("Playing", ps5.GetProperty("status").GetString());
        Assert.Equal(55_000, ps5.GetProperty("amountToman").GetInt64());
        var pc = grid.EnumerateArray().First(e => e.GetProperty("id").GetString() == "PC-FREE");
        Assert.Equal("Free", pc.GetProperty("status").GetString());
        Assert.True(pc.GetProperty("amountToman").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task s3_scenario_02_one_tap_start_with_defaults_and_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await op.PostAsync("/api/operator/stations/PS5-1/start", null);
        start.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.Station).SingleAsync();
        Assert.Equal(SessionStatus.Open, session.Status);
        Assert.Equal(2, session.PlayerCount);
        Assert.Equal(S3TestHelpers.T(2026, 6, 3, 14, 0).UtcDateTime, session.StartedAtUtc, TimeSpan.FromSeconds(1));
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "session.start");
        Assert.NotNull(await db.AuditLog.Where(a => a.Action == "session.start").Select(a => a.ActorUserId).FirstAsync());
    }

    [Fact]
    public async Task s3_scenario_03_console_pause_bill_confirm_110000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await _api.PostAsync($"/api/sessions/{sessionId}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await _api.PostAsync($"/api/sessions/{sessionId}/resume", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 110_000, 110_000);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(110_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task s3_scenario_04_pc_pause_api_returns_409()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.PostAsync($"/api/sessions/{sessionId}/pause", null)).StatusCode);
    }

    [Fact]
    public async Task s3_scenario_05_frozen_bill_confirm_450000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 17, 36);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 450_000, 450_000);
        factory.Clock.SetTehran(2026, 6, 3, 17, 40);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 450_000);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(450_000, invoice.CollectibleToman);
        Assert.Equal(S3TestHelpers.T(2026, 6, 3, 17, 36).UtcDateTime, invoice.BilledToUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(SessionStatus.Closed, (await db.Sessions.SingleAsync()).Status);
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        Assert.Equal("Free", grid.EnumerateArray().First(e => e.GetProperty("id").GetString() == "PS5-1").GetProperty("status").GetString());
    }

    [Fact]
    public async Task s3_scenario_06_cancel_bill_and_reopen_165000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/cancel", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 165_000, 165_000);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.CountAsync(a => a.Action == "bill.cancel"));
    }

    [Fact]
    public async Task s3_scenario_07_rounding_91667_collect_95000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        S3TestHelpers.AssertMoney(await bill.Content.ReadFromJsonAsync<JsonElement>(), 91_667, 95_000);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 95_000);
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(91_667, invoice.UnroundedPayableToman);
        Assert.Equal(95_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task s3_scenario_08_double_surcharge_collect_460000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 17, 36);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/surcharge", JsonContent.Create(new { reason = (string?)null }));
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/surcharge", JsonContent.Create(new { reason = (string?)null }));
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 460_000);
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(10_000, invoice.SurchargeToman);
        Assert.Equal(460_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task s3_scenario_09_split_payment_280000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 3);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 3);
        factory.Clock.SetTehran(2026, 6, 3, 15, 45);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Cash", amountToman = 200_000L },
                    new { method = "CardReader", amountToman = 80_000L },
                },
            });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PaymentLines.CountAsync());
    }

    [Fact]
    public async Task s3_scenario_10_card_to_card_pending_then_confirmed()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "card A" });
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "CardToCard", amountToman = 80_000L, destinationCardId = "card-a", reference = "1234" },
                },
            });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PaymentLines.SingleAsync();
        Assert.Equal(PaymentConfirmationStatus.Pending, line.ConfirmationStatus);
        (await op.PostAsync($"/api/operator/payments/{line.Id}/confirm", null)).EnsureSuccessStatusCode();
        db.ChangeTracker.Clear();
        line = await db.PaymentLines.SingleAsync();
        Assert.Equal(PaymentConfirmationStatus.Confirmed, line.ConfirmationStatus);
    }

    [Fact]
    public async Task s3_scenario_11_pc_logout_bill_80000_and_vip_130000()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-N", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var normalId = await S3TestHelpers.StartSession(_api, "PC-N", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, normalId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        var stop = await _api.PostAsync($"/api/sessions/{normalId}/stop", null);
        S3TestHelpers.AssertMoney(await stop.Content.ReadFromJsonAsync<JsonElement>(), 80_000, 80_000);

        await S3TestHelpers.RegisterPc(_api, "PC-V", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 2, 0);
        var vipId = await S3TestHelpers.StartSession(_api, "PC-V", 1);
        factory.Clock.SetTehran(2026, 6, 3, 3, 0);
        await S3TestHelpers.CheckpointAsync(_api, vipId, S3TestHelpers.T(2026, 6, 3, 3, 0));
        stop = await _api.PostAsync($"/api/sessions/{vipId}/stop", null);
        S3TestHelpers.AssertMoney(await stop.Content.ReadFromJsonAsync<JsonElement>(), 130_000, 130_000);
    }

    [Fact]
    public async Task s3_scenario_12_dead_pc_auto_close_80000()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-D", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-D", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        await _api.PostAsync("/api/internal/dead-pc-sweep", null);
        using var scope = factory.Services.CreateScope();
        var pending = await scope.ServiceProvider.GetRequiredService<AppDbContext>().PendingBills.SingleAsync();
        Assert.Equal(80_000, pending.CollectibleToman);
        Assert.Equal("auto-close", pending.StopReason);
    }

    [Fact]
    public async Task s3_scenario_13_manual_settle_queue_options()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-MS1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id1 = await S3TestHelpers.StartSession(_api, "PC-MS1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, id1, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await S3TestHelpers.CheckpointAsync(_api, id1, S3TestHelpers.T(2026, 6, 3, 14, 30));
        var queue = await op.GetFromJsonAsync<JsonElement>("/api/operator/manual-settle-queue");
        Assert.Equal(1, queue.GetArrayLength());
        var a = await op.PostAsJsonAsync($"/api/operator/sessions/{id1}/manual-settle", new { });
        S3TestHelpers.AssertMoney(await a.Content.ReadFromJsonAsync<JsonElement>(), 80_000, 80_000);

        await S3TestHelpers.RegisterPc(_api, "PC-MS2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id2 = await S3TestHelpers.StartSession(_api, "PC-MS2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, id2, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await S3TestHelpers.CheckpointAsync(_api, id2, S3TestHelpers.T(2026, 6, 3, 14, 30));
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        var b = await op.PostAsJsonAsync($"/api/operator/sessions/{id2}/manual-settle", new { endAt = S3TestHelpers.T(2026, 6, 3, 15, 30) });
        S3TestHelpers.AssertMoney(await b.Content.ReadFromJsonAsync<JsonElement>(), 120_000, 120_000);

        await S3TestHelpers.RegisterPc(_api, "PC-MS3", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id3 = await S3TestHelpers.StartSession(_api, "PC-MS3", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, id3, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await S3TestHelpers.CheckpointAsync(_api, id3, S3TestHelpers.T(2026, 6, 3, 14, 30));
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        var reject = await op.PostAsJsonAsync($"/api/operator/sessions/{id3}/manual-settle", new { endAt = S3TestHelpers.T(2026, 6, 3, 15, 30) });
        Assert.Equal(HttpStatusCode.BadRequest, reject.StatusCode);
    }

    [Fact]
    public async Task s3_scenario_14_invoice_permissions_and_void()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 2, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 2, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var scope = factory.Services.CreateScope();
        var number = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.Select(i => i.Number).SingleAsync();
        var read = await op.GetAsync($"/api/operator/invoices/{number}");
        read.EnsureSuccessStatusCode();
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("readOnly").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/operator/invoices/{number}/void", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync($"/api/operator/invoices/{number}/void", new { reason = "" })).StatusCode);
        var voidRes = await manager.PostAsJsonAsync($"/api/operator/invoices/{number}/void", new { reason = "ثبت اشتباه" });
        voidRes.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task s3_scenario_15_no_account_play_cash_110000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(110_000, invoice.CollectibleToman);
    }
}

[Collection("server")]
public sealed class S3OperatorPanelUiTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task operator_static_ui_loads_and_grid_api_works_after_login()
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = await factory.CreateOperatorClientAsync();
        var html = await client.GetStringAsync("/operator/index.html");
        Assert.Contains("Station grid", html);
        var grid = await client.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        Assert.Equal(JsonValueKind.Array, grid.ValueKind);
    }
}
