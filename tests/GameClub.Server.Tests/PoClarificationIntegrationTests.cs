using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PoClarificationIntegrationTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientWithKey();

    private async Task ResetAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE rejected_card_to_card, payment_lines, pending_bills, audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, rate_card_snapshot, stations, destination_cards RESTART IDENTITY CASCADE;
            TRUNCATE operator_users RESTART IDENTITY CASCADE;
            SELECT setval('invoice_number_seq', 1, false);
            UPDATE invoice_number_sequence SET "NextNumber" = 1;
            UPDATE rate_lines SET "HourlyToman" = 80000
              WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Offline' AND "PlayerCount" = 1;
            """);
        await DatabaseSeed.EnsureAsync(db);
    }

    [Fact]
    public async Task stop_response_and_db_store_unrounded_and_collectible_amounts()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-INV", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-INV");
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 50) });
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var body = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(66_667, body.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(70_000, body.GetProperty("collectibleToman").GetInt64());
        await S3TestHelpers.FinishPcPaymentAsync(factory, sessionId, 70_000);

        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(66_667, invoice.UnroundedPayableToman);
        Assert.Equal(70_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task dead_pc_auto_close_at_exactly_ten_minutes_since_last_checkpoint()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-10M", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-10M");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });

        factory.Clock.SetTehran(2026, 6, 3, 15, 9, 59);
        var early = await _client.PostAsync("/api/internal/dead-pc-sweep", null);
        early.EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().PendingBills.CountAsync());
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 10, 0);
        var sweep = await _client.PostAsync("/api/internal/dead-pc-sweep", null);
        sweep.EnsureSuccessStatusCode();
        using var scope2 = factory.Services.CreateScope();
        var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = await db.PendingBills.SingleAsync();
        Assert.Equal(80_000, pending.CollectibleToman);
    }

    [Fact]
    public async Task manual_settle_default_bills_to_last_checkpoint_and_is_audited()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-MS", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-MS");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 30) });

        var settle = await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/manual-settle", new { });
        settle.EnsureSuccessStatusCode();
        var body = await settle.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80_000, body.GetProperty("collectibleToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("session.manual_settle_resolve", actions);
    }

    [Fact]
    public async Task manual_settle_with_operator_end_time_between_checkpoint_and_now()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-MS2", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-MS2");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 30) });

        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        var settle = await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/manual-settle", new { endAt = TestTimes.T(2026, 6, 3, 15, 20) });
        settle.EnsureSuccessStatusCode();
        var body = await settle.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(106_667, body.GetProperty("unroundedPayableToman").GetInt64());
    }

    [Fact]
    public async Task manual_settle_rejects_future_end_time_with_400()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-MS3", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-MS3");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 30) });

        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        var settle = await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/manual-settle", new { endAt = TestTimes.T(2026, 6, 3, 15, 30) });
        Assert.Equal(HttpStatusCode.BadRequest, settle.StatusCode);
    }

    [Fact]
    public async Task live_amount_uses_started_minute_ceiling_unrounded()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-LIVE", name = "PS5", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0, 0);
        var sessionId = await StartSession("PS5-LIVE", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 1, 5);
        var live = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/live-amount");
        Assert.Equal(3_667, live.GetProperty("amountToman").GetInt64());
    }

    private async Task<Guid> StartSession(string stationId, int playerCount = 1)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new { stationId, playerCount });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }
}
