using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// PR #5 review round 1 blocking repros (B1–B5).
[Collection("server")]
public sealed class Pr5ReviewRound1BlockingTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task b1_pause_during_open_console_bill_returns_409_then_confirm_succeeds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-H", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await S3TestHelpers.StartSession(_api, "PS5-H");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        (await op.PostAsync($"/api/operator/sessions/{id}/bill/open", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 15, 5);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.PostAsync($"/api/sessions/{id}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _api.PatchAsJsonAsync($"/api/sessions/{id}/player-count", new { playerCount = 2 })).StatusCode);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{id}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = 110_000L } } });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.Include(i => i.PaymentLines).SingleAsync(i => i.SessionId == id);
        Assert.Equal(110_000, invoice.CollectibleToman);
        Assert.Single(invoice.PaymentLines);
    }

    [Fact]
    public async Task b2_grid_returns_200_when_manual_settle_and_new_session_share_station()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-E", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var a = await S3TestHelpers.StartSession(_api, "PC-E");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, a, TestTimes.T(2026, 6, 3, 15, 0));
        factory.Clock.SetTehran(2026, 6, 3, 15, 1);
        await S3TestHelpers.CheckpointAsync(_api, a, TestTimes.T(2026, 6, 3, 14, 30));
        (await op.PostAsync("/api/operator/stations/PC-E/start", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await op.GetAsync("/api/operator/grid")).StatusCode);
    }

    [Fact]
    public async Task b4_rejects_pc_balance_numeric_and_wallet_payment_methods()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await S3TestHelpers.StartSession(_api, "PS5-C");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{id}/bill/open", null);
        foreach (var pays in new object[][]
                 {
                     [new { method = "PcBalance", amountToman = 110_000L }],
                     [new { method = "4", amountToman = 110_000L }],
                     [new { method = "1", amountToman = 110_000L }],
                     [new { method = "Wallet", amountToman = 110_000L }],
                 })
        {
            var r = await op.PostAsJsonAsync($"/api/operator/sessions/{id}/bill/confirm", new { payments = pays });
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
    }

    [Fact]
    public async Task b5_console_machine_stop_creates_pending_bill_not_zero_line_invoice()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-S", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await S3TestHelpers.StartSession(_api, "PS5-S");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var stop = await _api.PostAsync($"/api/sessions/{id}/stop", null);
        stop.EnsureSuccessStatusCode();
        var body = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("pending").GetBoolean());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(SessionStatus.AwaitingPayment, (await db.Sessions.SingleAsync()).Status);
        await S3TestHelpers.ConfirmCashAsync(op, id, 110_000);
        Assert.Equal(1, await db.PaymentLines.CountAsync());
    }

    [Fact]
    public async Task b3_operator_pause_endpoint_accepts_session_cookie()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-P", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await S3TestHelpers.StartSession(_api, "PS5-P");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/operator/sessions/{id}/pause", null)).StatusCode);
    }
}

[Collection("server")]
public sealed class Pr5OperatorPanelUiFlowTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task operator_ui_assets_expose_frozen_scenario_controls()
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = await factory.CreateOperatorClientAsync();
        var html = await client.GetStringAsync("/operator/index.html");
        Assert.DoesNotContain("value=\"operator\"", html);
        var js = await client.GetStringAsync("/operator/app.js");
        Assert.Contains("/api/operator/sessions/", js);
        Assert.Contains("manual-settle", js);
        Assert.Contains("player-count", js);
        Assert.Contains("CardToCard", js);
        Assert.Contains("showError", js);
        Assert.Contains("pending-card-to-card", js);
        Assert.Contains("Bill to end time", js);
        Assert.Contains("hourlyToman", js);
        Assert.Contains("fmtTehran", js);
    }
}
