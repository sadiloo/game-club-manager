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
public sealed class ScopeGapIntegrationTests(GameClubWebApplicationFactory factory)
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
    public async Task rate_update_then_new_session_billed_at_new_rate_with_audit()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-RATE", name = "PC", groupId = PilotSeed.PcNormalGroupId });

        var putBody = new
        {
            roundingStepToman = 5_000L,
            roundingDirection = "Up",
            lines = new[]
            {
                new
                {
                    groupId = PilotSeed.PcNormalGroupId,
                    playMode = "Offline",
                    playerCount = (int?)1,
                    isSpecialBand = false,
                    hourlyToman = 90_000L,
                },
            },
        };
        var put = await _client.PutAsJsonAsync("/api/rates", putBody);
        put.EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-RATE", playerCount = 1 });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        var stop = await _client.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var invoice = await stop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(90_000, invoice.GetProperty("collectibleToman").GetInt64());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "rate.change");
    }

    [Fact]
    public async Task station_update_and_deactivate_are_audited_and_inactive_cannot_start()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-CRUD", name = "Old", groupId = PilotSeed.PcNormalGroupId });

        var patch = await _client.PatchAsJsonAsync("/api/stations/PC-CRUD", new { name = "New name" });
        patch.EnsureSuccessStatusCode();

        var deactivate = await _client.PostAsync("/api/stations/PC-CRUD/deactivate", null);
        deactivate.EnsureSuccessStatusCode();

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-CRUD", playerCount = 1 });
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);

        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("station.update", actions);
        Assert.Contains("station.deactivate", actions);
    }

    [Fact]
    public async Task hosted_dead_pc_sweep_auto_closes_with_fake_clock()
    {
        await ResetAsync();
        await _client.PostAsJsonAsync("/api/stations", new { id = "PC-SWEEP", name = "PC", groupId = PilotSeed.PcNormalGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var start = await _client.PostAsJsonAsync("/api/sessions", new { stationId = "PC-SWEEP", playerCount = 1 });
        var sessionId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await _client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);

        PendingBillEntity? pending = null;
        for (var attempt = 0; attempt < 40 && pending is null; attempt++)
        {
            await Task.Delay(250);
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            pending = await db.PendingBills.FirstOrDefaultAsync();
        }

        Assert.NotNull(pending);
        Assert.Equal(80_000, pending!.CollectibleToman);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditLog.Where(a => a.Action == "session.stop").SingleAsync();
            Assert.Contains("auto-close", audit.DetailsJson);
        }
    }
}
