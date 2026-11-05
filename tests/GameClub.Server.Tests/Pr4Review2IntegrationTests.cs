using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Pr4Review2IntegrationTests(GameClubWebApplicationFactory factory)
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
    public async Task parallel_start_and_group_change_never_poison_segment_group()
    {
        await ResetAsync();
        var poisoned = 0;
        for (var i = 0; i < 40; i++)
        {
            var st = $"PC-R2-{i}";
            await _client.PostAsJsonAsync("/api/stations", new { id = st, name = st, groupId = PilotSeed.PcNormalGroupId });
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            using var gate = new ManualResetEventSlim(false);
            var tStart = Task.Run(async () =>
            {
                gate.Wait();
                return await _client.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 });
            });
            var tGroup = Task.Run(async () =>
            {
                gate.Wait();
                return await _client.PatchAsJsonAsync($"/api/stations/{st}/group", new { groupId = PilotSeed.PcVipGroupId });
            });
            gate.Set();
            await Task.WhenAll(tStart, tGroup);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var station = await db.Stations.AsNoTracking().SingleAsync(s => s.Id == st);
            var segment = await db.Segments.AsNoTracking().FirstOrDefaultAsync(s => s.StationId == st);
            if (segment is null)
            {
                continue;
            }

            if (!string.Equals(segment.GroupId, station.GroupId, StringComparison.Ordinal))
            {
                poisoned++;
            }
        }

        Assert.Equal(0, poisoned);
    }

    [Fact]
    public async Task failed_dead_pc_sweep_close_leaves_session_open_without_invoice()
    {
        await ResetAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ids = new List<Guid>();
        foreach (var st in new[] { "PC-F0", "PC-F1", "PC-F2", "PC-F3" })
        {
            await _client.PostAsJsonAsync("/api/stations", new { id = st, name = st, groupId = PilotSeed.PcNormalGroupId });
            var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 });
            ids.Add((await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        foreach (var id in ids)
        {
            await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        }

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await setupDb.Database.ExecuteSqlRawAsync(
                """UPDATE stations SET "GroupId" = {0} WHERE "Id" IN ('PC-F0','PC-F2')""",
                PilotSeed.PcVipGroupId);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        await _client.PostAsync("/api/internal/dead-pc-sweep", null);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var st in new[] { "PC-F0", "PC-F2" })
        {
            var session = await db.Sessions.SingleAsync(s => s.StationId == st);
            Assert.Equal(SessionStatus.Open, session.Status);
            Assert.Equal(0, await db.Invoices.CountAsync(i => i.SessionId == session.Id));
        }

        foreach (var st in new[] { "PC-F1", "PC-F3" })
        {
            var session = await db.Sessions.SingleAsync(s => s.StationId == st);
            Assert.Equal(SessionStatus.AwaitingPayment, session.Status);
            Assert.Equal(80_000, await db.PendingBills.Where(i => i.SessionId == session.Id).Select(i => i.CollectibleToman).SingleAsync());
        }
    }

    [Fact]
    public async Task concurrent_player_count_change_and_stop_yield_one_invoice_and_rest_409()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PS5-CN-R", name = "PS5-CN-R", groupId = GameTimerReference.Ps5GroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-CN-R", playerCount = 2 });
        var id = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var responses = await Task.WhenAll(
            _client.PatchAsJsonAsync($"/api/sessions/{id}/player-count", new { playerCount = 1 }),
            _client.PostAsync($"/api/sessions/{id}/stop", null),
            _client.PostAsync($"/api/sessions/{id}/stop", null));
        var codes = responses.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, codes.Count(c => c is 200 or 204));
        Assert.True(codes.All(c => c is 200 or 204 or 409));
        if (!responses.Any(r => r.StatusCode == HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.PendingBills.CountAsync(p => p.SessionId == id));
        Assert.Equal(0, await db.Invoices.CountAsync(i => i.SessionId == id));
    }

    [Fact]
    public async Task step2_pr4_review1_migration_backfills_rate_card_snapshot_for_existing_session()
    {
        var baseConn = factory.ConnectionString;
        var builder = new NpgsqlConnectionStringBuilder(baseConn);
        var dbName = "gc_pr4_mig_" + Guid.NewGuid().ToString("N")[..12];
        builder.Database = "postgres";
        await using (var admin = new NpgsqlConnection(builder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        builder.Database = dbName;
        var targetConn = builder.ConnectionString;
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(targetConn).Options;
            await using var db = new AppDbContext(options);
            await db.Database.MigrateAsync("20261104134405_Step2PoClarifications");
            await DatabaseSeed.EnsureAsync(db);

            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO stations ("Id", "Name", "GroupId", "IsActive")
                VALUES ('PC-OLD', 'PC-OLD', {0}, true)
                ON CONFLICT ("Id") DO NOTHING;
                """, PilotSeed.PcNormalGroupId);
            var sessionId = Guid.NewGuid();
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO sessions ("Id", "StationId", "Status", "StartedAtUtc", "EndedAtUtc", "Mode", "PlayerCount")
                VALUES ({0}, 'PC-OLD', 'Closed', NOW() - INTERVAL '1 hour', NOW(), 'Offline', 1);
                """, sessionId);

            await db.Database.MigrateAsync();

            var snapId = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.RateCardSnapshotId).SingleAsync();
            Assert.True(snapId > 0);
        }
        finally
        {
            builder.Database = "postgres";
            await using var admin = new NpgsqlConnection(builder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
