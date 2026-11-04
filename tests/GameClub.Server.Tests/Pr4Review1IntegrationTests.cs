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
public sealed class Pr4Review1IntegrationTests(GameClubWebApplicationFactory factory)
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
            """);
    }

    [Fact]
    public async Task concurrent_start_on_one_station_yields_one_201_and_rest_409()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-RACE", name = "PS5-RACE", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => _client.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-RACE", playerCount = 1 })));
        var codes = responses.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c == 201));
        Assert.True(codes.All(c => c is 201 or 409));
    }

    [Fact]
    public async Task group_change_while_session_active_returns_409_station_busy()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-G", name = "PC-G", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-G", playerCount = 1 });
        var patch = await _client.PatchAsJsonAsync("/api/stations/PC-G/group", new { groupId = PilotSeed.PcVipGroupId });
        Assert.Equal(HttpStatusCode.Conflict, patch.StatusCode);
        var body = await patch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("station_busy", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task poisoned_session_does_not_block_dead_pc_sweep_on_other_stations()
    {
        await ResetAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ids = new List<Guid>();
        foreach (var st in new[] { "PC-J0", "PC-J1", "PC-J2" })
        {
            await _client.PostAsJsonAsync("/api/stations", new { id = st, name = st, groupId = PilotSeed.PcNormalGroupId });
            var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 });
            ids.Add((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        foreach (var id in ids)
        {
            await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { });
        }

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await setupDb.Database.ExecuteSqlRawAsync(
                """UPDATE play_segments SET "GroupId" = {0} WHERE "SessionId" = {1} AND "EndUtc" IS NULL""",
                PilotSeed.PcVipGroupId,
                ids[0]);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        await _client.PostAsync("/api/internal/dead-pc-sweep", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatus.Closed, await db.Sessions.Where(s => s.StationId == "PC-J1").Select(s => s.Status).SingleAsync());
        Assert.Equal(SessionStatus.Closed, await db.Sessions.Where(s => s.StationId == "PC-J2").Select(s => s.Status).SingleAsync());
    }

    [Fact]
    public async Task manual_settle_session_does_not_block_new_start_on_same_station()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-MS", name = "PC-MS", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-MS", playerCount = 1 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { });
        factory.Clock.SetTehran(2026, 6, 3, 15, 1);
        await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 30) });
        factory.Clock.SetTehran(2026, 6, 3, 15, 5);
        var again = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-MS", playerCount = 1 });
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task rates_locked_at_session_start_survive_mid_session_rate_change()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-R", name = "PC-R", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-R", playerCount = 1 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await _client.PutAsJsonAsync("/api/rates", new
        {
            roundingStepToman = 5000L,
            roundingDirection = "Up",
            lines = new[] { new { groupId = PilotSeed.PcNormalGroupId, playMode = "Offline", playerCount = (int?)1, isSpecialBand = false, hourlyToman = 120_000L } },
        });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { });
        var stop = await _client.PostAsync($"/api/sessions/{id}/stop", null);
        var inv = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80_000, inv.GetProperty("unroundedPayableToman").GetInt64());

        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        var start2 = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-R", playerCount = 1 });
        var id2 = (await start2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 17, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{id2}/checkpoints", new { });
        var stop2 = await _client.PostAsync($"/api/sessions/{id2}/stop", null);
        var inv2 = await stop2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(120_000, inv2.GetProperty("unroundedPayableToman").GetInt64());
    }

    [Fact]
    public async Task client_started_at_is_rejected_with_400()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-T", name = "PS5-T", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var r = await _client.PostAsJsonAsync("/api/sessions", new
        {
            stationId = "PS5-T",
            playerCount = 1,
            startedAt = TestTimes.T(2026, 6, 3, 12, 0),
        });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task checkpoint_records_server_time_and_ignores_client_at_for_billing()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-T", name = "PC-T", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-T", playerCount = 1 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 18, 0) });
        var stop = await _client.PostAsync($"/api/sessions/{id}/stop", null);
        var inv = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80_000, inv.GetProperty("unroundedPayableToman").GetInt64());
    }

    [Fact]
    public async Task concurrent_pause_creates_at_most_one_open_pause_interval()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-P", name = "PS5-P", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-P", playerCount = 1 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _client.PostAsync($"/api/sessions/{id}/pause", null)));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Pauses.CountAsync());
    }

    [Fact]
    public async Task concurrent_stop_same_session_one_ok_rest_409()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-C", name = "PS5-C", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-C", playerCount = 1 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _client.PostAsync($"/api/sessions/{id}/stop", null)));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c == 200));
        Assert.True(codes.All(c => c is 200 or 409));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task rate_update_rejects_negative_rate_bad_step_and_unknown_direction()
    {
        await ResetAsync();
        var neg = await _client.PutAsJsonAsync("/api/rates", new
        {
            roundingStepToman = 5000L,
            roundingDirection = "Up",
            lines = new[] { new { groupId = PilotSeed.PcNormalGroupId, playMode = "Offline", playerCount = (int?)1, isSpecialBand = false, hourlyToman = -1L } },
        });
        var zero = await _client.PutAsJsonAsync("/api/rates", new { roundingStepToman = 0L, roundingDirection = "Up", lines = Array.Empty<object>() });
        var dir = await _client.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000L, roundingDirection = "Sideways", lines = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, neg.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dir.StatusCode);
    }
}
