using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// PR #5 review round 2 (N1, B3a–c, tenders).
[Collection("server")]
public sealed class Pr5ReviewRound2Tests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task n1_pc_logout_inside_grace_confirms_empty_payment_and_frees_station()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-Z", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-Z");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0, 40);
        var stop = await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        stop.EnsureSuccessStatusCode();
        var body = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("collectibleToman").GetInt64());

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = Array.Empty<object>() });
        confirm.EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(0, invoice.CollectibleToman);
        Assert.Empty(invoice.PaymentLines);
        Assert.Equal(SessionStatus.Closed, (await db.Sessions.SingleAsync()).Status);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "bill.confirm"));

        (await op.PostAsync("/api/operator/stations/PC-Z/start", null)).EnsureSuccessStatusCode();
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = grid.EnumerateArray().First(e => e.GetProperty("id").GetString() == "PC-Z");
        Assert.Equal("Playing", tile.GetProperty("status").GetString());
    }

    [Fact]
    public async Task n1_console_machine_stop_at_start_zero_confirm_frees_station()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-Z2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-Z2");
        var stop = await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        stop.EnsureSuccessStatusCode();
        var body = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("collectibleToman").GetInt64());

        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = Array.Empty<object>() })).EnsureSuccessStatusCode();

        (await op.PostAsync("/api/operator/stations/PS5-Z2/start", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task n1_non_zero_bill_still_rejects_empty_payments()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-NZ", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-NZ");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task b3a_card_to_card_stays_pending_until_operator_confirms_with_audit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-C2C", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-C2C");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "CardToCard", amountToman = 80_000L, destinationCardId = "card-a" } } }))
            .EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.PaymentLines.SingleAsync();
        Assert.Equal(PaymentConfirmationStatus.Pending, line.ConfirmationStatus);

        var pending = await op.GetFromJsonAsync<JsonElement>("/api/operator/payments/pending-card-to-card");
        Assert.Equal(1, pending.GetArrayLength());

        (await op.PostAsync($"/api/operator/payments/{line.Id}/confirm", null)).EnsureSuccessStatusCode();
        db.ChangeTracker.Clear();
        line = await db.PaymentLines.SingleAsync();
        Assert.Equal(PaymentConfirmationStatus.Confirmed, line.ConfirmationStatus);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "payment.card_to_card_confirmed"));
    }

    [Fact]
    public async Task b3b_manual_settle_option_b_end_time_120000_from_panel_api()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-MS2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-MS2");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 14, 30));
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        var bill = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/manual-settle",
            new { endAt = S3TestHelpers.T(2026, 6, 3, 15, 30) });
        bill.EnsureSuccessStatusCode();
        var detail = await bill.Content.ReadFromJsonAsync<JsonElement>();
        S3TestHelpers.AssertMoney(detail, 120_000, 120_000);
        Assert.True(detail.TryGetProperty("segments", out var segs) && segs.GetArrayLength() > 0);
        Assert.True(segs[0].TryGetProperty("hourlyToman", out var rate) && rate.GetInt64() == 80_000);
    }

    [Fact]
    public async Task tenders_accept_cash_cardreader_cardtocard_case_insensitively_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-T", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await S3TestHelpers.StartSession(_api, "PS5-T");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{id}/bill/open", null);

        foreach (var method in new[] { "cash", "CASH", "Cash" })
        {
            await S3TestHelpers.ResetAsync(factory);
            var opCase = await factory.CreateOperatorClientAsync();
            await S3TestHelpers.RegisterConsole(_api, opCase, "PS5-T", 1);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            id = await S3TestHelpers.StartSession(_api, "PS5-T");
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            await opCase.PostAsync($"/api/operator/sessions/{id}/bill/open", null);
            var ok = await opCase.PostAsJsonAsync(
                $"/api/operator/sessions/{id}/bill/confirm",
                new { payments = new[] { new { method, amountToman = 110_000L } } });
            Assert.True(ok.IsSuccessStatusCode, $"{method}: {await ok.Content.ReadAsStringAsync()}");
        }

        await S3TestHelpers.ResetAsync(factory);
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-T2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        id = await S3TestHelpers.StartSession(_api, "PS5-T2");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{id}/bill/open", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{id}/bill/confirm",
            new { payments = new[] { new { method = " CASH", amountToman = 110_000L } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{id}/bill/confirm",
            new { payments = new[] { new { method = "1", amountToman = 110_000L } } })).StatusCode);
    }
}
