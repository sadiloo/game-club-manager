using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Tests;

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<GameClubWebApplicationFactory>;

[Collection("server")]
public sealed class AcceptanceScenarioTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientWithKey();

    private async Task ResetAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, rate_card_snapshot, stations RESTART IDENTITY CASCADE;
            SELECT setval('invoice_number_seq', 1, false);
            UPDATE invoice_number_sequence SET "NextNumber" = 1;
            UPDATE rate_lines SET "HourlyToman" = 80000
              WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Offline' AND "PlayerCount" = 1;
            """);
    }

    [Fact]
    public async Task scenario_01_register_normalizes_id_and_rejects_duplicate()
    {
        await ResetAsync();
        var first = await _client.PostAsJsonAsync("/api/stations", new { id = " PC-۰۱ ", name = "PC 1", groupId = PilotSeed.PcNormalGroupId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PC-01", body.GetProperty("id").GetString());

        var dup = await _client.PostAsJsonAsync("/api/stations", new { id = "pc-01", name = "dup", groupId = PilotSeed.PcNormalGroupId });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var count = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Stations.CountAsync();
            Assert.Equal(1, count);
        }
    }

    [Fact]
    public async Task scenario_02_normal_pc_one_hour_is_80000()
    {
        await ResetAsync();
        await RegisterPc("PC-A", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-A", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await CheckpointAsync(sessionId);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 80_000, 80_000);
    }

    [Fact]
    public async Task scenario_03_directory_group_vip_one_hour_is_110000()
    {
        await ResetAsync();
        await RegisterPc("PC-B", PilotSeed.PcNormalGroupId);
        await _client.PatchAsJsonAsync("/api/stations/PC-B/group", new { groupId = PilotSeed.PcVipGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-B", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await CheckpointAsync(sessionId);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 110_000, 110_000);
    }

    [Fact]
    public async Task scenario_04_normal_pc_fifty_minutes_rounds_to_70000()
    {
        await ResetAsync();
        await RegisterPc("PC-C", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await CheckpointAsync(sessionId);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 66_667, 70_000);
    }

    [Fact]
    public async Task scenario_05_normal_pc_crosses_special_band_to_90000()
    {
        await ResetAsync();
        await RegisterPc("PC-D", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 8, 0);
        var sessionId = await StartSession("PC-D", 1);
        factory.Clock.SetTehran(2026, 6, 3, 9, 0);
        await CheckpointAsync(sessionId);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 90_000, 90_000);
    }

    [Fact]
    public async Task scenario_06_vip_pc_special_hour_is_130000()
    {
        await ResetAsync();
        await RegisterPc("PC-E", PilotSeed.PcVipGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 2, 0);
        var sessionId = await StartSession("PC-E", 1);
        factory.Clock.SetTehran(2026, 6, 3, 3, 0);
        await CheckpointAsync(sessionId);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 130_000, 130_000);
    }

    [Fact]
    public async Task scenario_07_ps5_two_players_14_00_to_17_36_is_450000()
    {
        await ResetAsync();
        await RegisterConsole("PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PS5-1", 2);
        factory.Clock.SetTehran(2026, 6, 3, 17, 36);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 450_000, 450_000);
    }

    [Fact]
    public async Task scenario_08_ps5_three_players_14_00_to_15_45_is_280000()
    {
        await ResetAsync();
        await RegisterConsole("PS5-2", 3);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PS5-2", 3);
        factory.Clock.SetTehran(2026, 6, 3, 15, 45);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 280_000, 280_000);
    }

    [Fact]
    public async Task scenario_09_ps5_pause_resume_stop_audited()
    {
        await ResetAsync();
        await RegisterConsole("PS5-3", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PS5-3", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/sessions/{sessionId}/pause", null)).StatusCode);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        Assert.Equal(HttpStatusCode.NoContent, (await _client.PostAsync($"/api/sessions/{sessionId}/resume", null)).StatusCode);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 110_000, 110_000);
        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("session.pause", actions);
        Assert.Contains("session.resume", actions);
    }

    [Fact]
    public async Task scenario_10_pause_on_pc_returns_409_and_time_keeps_running()
    {
        await ResetAsync();
        await RegisterPc("PC-F", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-F", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var pause = await _client.PostAsync($"/api/sessions/{sessionId}/pause", null);
        Assert.Equal(HttpStatusCode.Conflict, pause.StatusCode);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await CheckpointAsync(sessionId);
        var live = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/live-amount");
        Assert.Equal(80_000, live.GetProperty("amountToman").GetInt64());
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 80_000, 80_000);
    }

    [Fact]
    public async Task scenario_11_live_amount_at_14_30_is_55000_unrounded()
    {
        await ResetAsync();
        await RegisterConsole("PS5-4", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PS5-4", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var live = await _client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}/live-amount");
        Assert.Equal(55_000, live.GetProperty("amountToman").GetInt64());
    }

    [Fact]
    public async Task scenario_12_started_minute_rounds_to_5000_on_close()
    {
        await ResetAsync();
        await RegisterConsole("PS5-5", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0, 0);
        var sessionId = await StartSession("PS5-5", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 1, 5);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 3_667, 5_000);
    }

    [Fact]
    public async Task scenario_13_dead_pc_auto_closes_to_last_checkpoint()
    {
        await ResetAsync();
        await RegisterPc("PC-G", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-G", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await CheckpointAsync(sessionId);
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        var sweep = await _client.PostAsync("/api/internal/dead-pc-sweep", null);
        Assert.Equal(HttpStatusCode.OK, sweep.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(80_000, invoice.UnroundedPayableToman);
        Assert.Equal(80_000, invoice.CollectibleToman);
        var audit = await db.AuditLog.Where(a => a.Action == "session.stop").SingleAsync();
        Assert.Contains("auto-close", audit.DetailsJson);
    }

    [Fact]
    public async Task scenario_14_rejected_checkpoint_goes_to_manual_settle_without_invoice()
    {
        await ResetAsync();
        await RegisterPc("PC-H", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-H", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await CheckpointAsync(sessionId);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 30) });
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        Assert.Equal(HttpStatusCode.Conflict, stop.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
        var session = await db.Sessions.SingleAsync();
        Assert.Equal(SessionStatus.ManualSettle, session.Status);
    }

    [Fact]
    public async Task scenario_15_audit_is_append_only_in_app_and_database()
    {
        await ResetAsync();
        await RegisterPc("PC-I", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PC-I", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 5);
        await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "session.start"));
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "session.stop"));
        var updateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync("UPDATE audit_log SET \"Action\" = 'tamper' WHERE \"Id\" = 1"));
        Assert.Equal("P0001", updateEx.SqlState);
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            await db.Database.ExecuteSqlRawAsync("DELETE FROM audit_log WHERE \"Id\" = 1"));
        Assert.Equal("P0001", deleteEx.SqlState);

        var row = await db.AuditLog.AsNoTracking().FirstAsync();
        var tracked = await db.AuditLog.FirstAsync(a => a.Id == row.Id);
        tracked.Action = "tamper-via-ef";
        await Assert.ThrowsAsync<AuditLogAppendOnlyException>(() => db.SaveChangesAsync());
        db.Entry(tracked).State = EntityState.Unchanged;
        db.AuditLog.Remove(tracked);
        await Assert.ThrowsAsync<AuditLogAppendOnlyException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task cn_3_changing_controller_count_mid_session_splits_pricing()
    {
        await ResetAsync();
        await RegisterConsole("PS5-CN", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await StartSession("PS5-CN", 2);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PatchAsJsonAsync($"/api/sessions/{sessionId}/player-count", new { playerCount = 1 });
        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        AssertInvoice(invoice, 235_000, 235_000);
    }

    private static void AssertInvoice(JsonElement invoice, long unrounded, long collectible)
    {
        Assert.Equal(unrounded, invoice.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(collectible, invoice.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(collectible - unrounded, invoice.GetProperty("roundingToman").GetInt64());
    }

    private static void AssertInvoice(long unrounded, long collectible, long expectedUnrounded, long expectedCollectible)
    {
        Assert.Equal(expectedUnrounded, unrounded);
        Assert.Equal(expectedCollectible, collectible);
    }

    private Task CheckpointAsync(Guid sessionId)
        => _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { });

    private async Task RegisterPc(string id, string groupId)
    {
        var response = await _client.PostAsJsonAsync("/api/stations", new { id, name = id, groupId });
        response.EnsureSuccessStatusCode();
    }

    private async Task RegisterConsole(string id, int players)
    {
        var response = await _client.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = GameTimerReference.Ps5GroupId });
        response.EnsureSuccessStatusCode();
        _ = players;
    }

    private async Task<Guid> StartSession(string stationId, int playerCount)
    {
        var response = await _client.PostAsJsonAsync("/api/sessions", new { stationId, playerCount });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }
}
