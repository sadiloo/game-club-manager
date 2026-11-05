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
public sealed class Ds13CheckpointRaceIntegrationTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClientWithKey();

    private HttpClient Api() => factory.CreateClientWithKey();

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

    private async Task RegAsync(string id)
        => (await _client.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = PilotSeed.PcNormalGroupId }))
            .EnsureSuccessStatusCode();

    private async Task<Guid> StartAsync(string stationId)
    {
        var r = await _client.PostAsJsonAsync("/api/sessions", new { stationId = stationId, playerCount = 1 });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<string> SessionStatusAsync(Guid id)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id)).Status.ToString();
    }

    private async Task<long> EpochAsync(Guid id, string column)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id);
        var dt = column switch
        {
            "LastCheckpointUtc" => session.LastCheckpointUtc,
            "EndedAtUtc" => session.EndedAtUtc,
            _ => throw new ArgumentOutOfRangeException(nameof(column)),
        };
        return dt is null ? 0 : new DateTimeOffset(DateTime.SpecifyKind(dt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }

    [Fact]
    public async Task stop_vs_backwards_checkpoint_race_ends_closed_with_one_invoice_or_manual_settle_with_none()
    {
        await ResetAsync();
        var bad = new List<string>();
        for (var round = 0; round < 30; round++)
        {
            var st = $"PC-RACE{round}";
            await RegAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await StartAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            (await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).EnsureSuccessStatusCode();
            factory.Clock.SetTehran(2026, 6, 3, 15, 1);
            await Task.WhenAll(
                Api().PostAsync($"/api/sessions/{id}/stop", null),
                Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 0) }));

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == id);
            var inv = await db.Invoices.CountAsync(i => i.SessionId == id);
            var ok = (session.Status == SessionStatus.Closed && inv == 1)
                || (session.Status == SessionStatus.ManualSettle && inv == 0);
            if (!ok)
            {
                bad.Add($"r{round}: status={session.Status} invoices={inv}");
            }
        }

        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    [Fact]
    public async Task qa_b1_stop_and_backwards_checkpoint_race_never_corrupts_closed_session()
    {
        await ResetAsync();
        var bad = new List<string>();
        for (var round = 0; round < 20; round++)
        {
            var st = $"PC-B1{round}";
            await RegAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await StartAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            (await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).EnsureSuccessStatusCode();
            factory.Clock.SetTehran(2026, 6, 3, 15, 1);
            var stopTask = Api().PostAsync($"/api/sessions/{id}/stop", null);
            var cpTask = Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 0) });
            await Task.WhenAll(stopTask, cpTask);
            var stopResponse = await stopTask;
            var cpResponse = await cpTask;
            var status = await SessionStatusAsync(id);
            if (status != "Closed" && stopResponse.StatusCode == HttpStatusCode.OK)
            {
                var m = await _client.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { });
                bad.Add($"r{round}: stop={(int)stopResponse.StatusCode} cp={(int)cpResponse.StatusCode} status={status} resolve={(int)m.StatusCode}");
            }
        }

        Assert.True(bad.Count == 0, $"{bad.Count}/20 corrupted: " + string.Join(" | ", bad.Take(3)));
    }

    [Fact]
    public async Task qa_h10_checkpoint_vs_stop_race_closed_session_stays_consistent()
    {
        await ResetAsync();
        var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PC-CS{round}";
            await RegAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await StartAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            (await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).EnsureSuccessStatusCode();
            factory.Clock.SetTehran(2026, 6, 3, 15, 1);
            var tasks = Enumerable.Range(0, 32).Select(i => i % 2 == 0
                ? Api().PostAsync($"/api/sessions/{id}/stop", null)
                : (i % 4 == 1
                    ? Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })
                    : Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 0) })));
            var codes = (await Task.WhenAll(tasks)).Select(r => (int)r.StatusCode).ToList();
            var status = await SessionStatusAsync(id);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var inv = await db.Invoices.CountAsync(i => i.SessionId == id);
                var lastCp = await EpochAsync(id, "LastCheckpointUtc");
                var billedEnd = await EpochAsync(id, "EndedAtUtc");
                if (codes.Any(c => c >= 500) || (inv == 1 && status != "Closed") || (inv == 1 && lastCp != billedEnd))
                {
                    bad.Add($"r{round}: status={status} invoices={inv} lastCp-billedEnd={lastCp - billedEnd}s codes=[{string.Join(',', codes)}]");
                }
            }
        }

        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    [Fact]
    public async Task qa_b2_sweep_and_checkpoint_race_never_closes_at_stale_checkpoint()
    {
        await ResetAsync();
        var bad = new List<string>();
        for (var round = 0; round < 20; round++)
        {
            var st = $"PC-B2{round}";
            await RegAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await StartAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            (await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).EnsureSuccessStatusCode();
            factory.Clock.SetTehran(2026, 6, 3, 15, 10);
            var sweepTask = Api().PostAsync("/api/internal/dead-pc-sweep", null);
            var cpTask = Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { });
            await Task.WhenAll(sweepTask, cpTask);
            var cpResponse = await cpTask;
            var status = await SessionStatusAsync(id);
            if (status == "Closed" && cpResponse.StatusCode == HttpStatusCode.NoContent)
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var billed = await db.Invoices.Where(i => i.SessionId == id).Select(i => i.UnroundedPayableToman).FirstOrDefaultAsync();
                if (billed == 80_000)
                {
                    bad.Add($"r{round}: cp=204 but closed billed {billed}");
                }
            }
        }

        Assert.True(bad.Count == 0, $"{bad.Count}/20: " + string.Join(" | ", bad.Take(3)));
    }

    [Fact]
    public async Task qa_h11_checkpoint_vs_sweep_race_accepted_checkpoint_not_lost()
    {
        await ResetAsync();
        var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PC-CW{round}";
            await RegAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await StartAsync(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            (await _client.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).EnsureSuccessStatusCode();
            factory.Clock.SetTehran(2026, 6, 3, 15, 10);
            var tasks = Enumerable.Range(0, 16).Select(i => i % 2 == 0
                ? Api().PostAsync("/api/internal/dead-pc-sweep", null)
                : Api().PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { }));
            var rs = await Task.WhenAll(tasks);
            var cpOk = rs.Where((r, i) => i % 2 == 1 && r.StatusCode == HttpStatusCode.NoContent).Count();
            var codes = rs.Select(r => (int)r.StatusCode).ToList();
            var status = await SessionStatusAsync(id);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var billed = await db.Invoices.Where(i => i.SessionId == id).Select(i => (long?)i.UnroundedPayableToman).MaxAsync() ?? -1;
                if (codes.Any(c => c >= 500) || (status == "Closed" && cpOk > 0 && billed == 80_000) || (status != "Closed" && status != "Open"))
                {
                    bad.Add($"r{round}: status={status} acceptedCheckpoints={cpOk} billed={billed} codes=[{string.Join(',', codes)}]");
                }
            }
        }

        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
}
