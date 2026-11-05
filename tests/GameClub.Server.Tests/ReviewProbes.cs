using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ReviewProbes(GameClubWebApplicationFactory factory, ITestOutputHelper o)
{
    private readonly HttpClient c = factory.CreateClientWithKey();

    private async Task Reset()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync("""
            TRUNCATE audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, stations RESTART IDENTITY CASCADE;
            UPDATE invoice_number_sequence SET "NextNumber" = 1;
            UPDATE rate_lines SET "HourlyToman" = 80000 WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false;
            """);
    }

    private async Task Reg(string id, string g) => (await c.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = g })).EnsureSuccessStatusCode();
    private async Task<Guid> Start(string st, int p = 1, string mode = "Offline")
    {
        var r = await c.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = p, mode });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private async Task<string> Show(HttpResponseMessage r) { var b = await r.Content.ReadAsStringAsync(); return $"{(int)r.StatusCode} {b[..Math.Min(b.Length, 160)]}"; }
    private T Db<T>(Func<AppDbContext, T> f) { using var s = factory.Services.CreateScope(); return f(s.ServiceProvider.GetRequiredService<AppDbContext>()); }
    private void Log(string s) { o.WriteLine(s); File.AppendAllText(Path.Combine(Path.GetTempPath(), "pr4-probes-out.txt"), s + "\n"); }

    [Fact] public async Task P01_group_change_mid_session()
    {
        await Reset(); Log("== P01 mid-session group change (Normal->VIP) ==");
        await Reg("PC-X", PilotSeed.PcNormalGroupId); await Reg("PC-Y", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sx = await Start("PC-X"); var sy = await Start("PC-Y");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Log("patch group: " + await Show(await c.PatchAsJsonAsync("/api/stations/PC-X/group", new { groupId = PilotSeed.PcVipGroupId })));
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        Log("checkpoint: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{sx}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) })));
        await c.PostAsJsonAsync($"/api/sessions/{sy}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        try { Log("live: " + await Show(await c.GetAsync($"/api/sessions/{sx}/live-amount"))); } catch (Exception e) { Log("live threw: " + e.GetType().Name + " " + e.Message); }
        try { Log("stop: " + await Show(await c.PostAsync($"/api/sessions/{sx}/stop", null))); } catch (Exception e) { Log("stop threw: " + e.GetType().Name + " " + e.Message); }
        Log("status after stop: " + Db(d => d.Sessions.Single(s => s.Id == sx).Status) + " invoices=" + Db(d => d.Invoices.Count()));
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        try { Log("manual sweep: " + await Show(await c.PostAsync("/api/internal/dead-pc-sweep", null))); } catch (Exception e) { Log("sweep threw: " + e.GetType().Name + " " + e.Message); }
        await Task.Delay(2500); // hosted sweep runs every 1 s in tests
        Log($"after sweeps: PC-X={Db(d => d.Sessions.Single(s => s.Id == sx).Status)} PC-Y={Db(d => d.Sessions.Single(s => s.Id == sy).Status)} invoices={Db(d => d.Invoices.Count())}");
    }

    [Fact] public async Task P02_concurrent_start_same_station()
    {
        await Reset(); Log("== P02 8 concurrent starts on one PS5 ==");
        await Reg("PS5-C", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        for (var round = 0; round < 5; round++)
        {
            var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => c.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-C", playerCount = 1 }))));
            var open = Db(d => d.Sessions.Count(s => s.StationId == "PS5-C" && s.Status != SessionStatus.Closed));
            Log($"round {round}: statuses={string.Join(",", rs.Select(r => (int)r.StatusCode))} open sessions on PS5-C={open}");
            if (open > 1)
            {
                factory.Clock.SetTehran(2026, 6, 3, 15, 0);
                var ids = Db(d => d.Sessions.Where(s => s.StationId == "PS5-C" && s.Status != SessionStatus.Closed).Select(s => s.Id).ToList());
                foreach (var id in ids) Log("  stop " + id + ": " + await Show(await c.PostAsync($"/api/sessions/{id}/stop", null)));
                Log("  total collectible for one station-hour: " + Db(d => d.Invoices.Sum(i => i.CollectibleToman)));
                break;
            }
            foreach (var id in Db(d => d.Sessions.Where(s => s.Status != SessionStatus.Closed).Select(s => s.Id).ToList())) await c.PostAsync($"/api/sessions/{id}/stop", null);
        }
    }

    [Fact] public async Task P03_concurrent_stop_same_session()
    {
        await Reset(); Log("== P03 8 concurrent stops of one session ==");
        await Reg("PS5-D", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await Start("PS5-D");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { try { return (int)(await c.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode; } catch (Exception e) { return -1 * e.GetType().Name.Length; } })));
        Log($"statuses={string.Join(",", rs)} invoices={Db(d => d.Invoices.Count())} sum={Db(d => d.Invoices.Sum(i => i.CollectibleToman))} nextNo={Db(d => d.InvoiceNumbers.Single().NextNumber)}");
    }

    [Fact] public async Task P04_concurrent_stops_different_sessions()
    {
        await Reset(); Log("== P04 6 sessions stopped concurrently ==");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) { await Reg($"PS5-{i}", GameTimerReference.Ps5GroupId); ids.Add(await Start($"PS5-{i}")); }
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var rs = await Task.WhenAll(ids.Select(id => Task.Run(async () => { try { var r = await c.PostAsync($"/api/sessions/{id}/stop", null); return await Show(r); } catch (Exception e) { return "EXC " + e.GetType().Name + ": " + e.Message[..Math.Min(120, e.Message.Length)]; } })));
        foreach (var r in rs) Log("  " + r);
        Log($"invoices={Db(d => d.Invoices.Count())} numbers={string.Join(",", Db(d => d.Invoices.OrderBy(i => i.Number).Select(i => i.Number).ToList()))} stillOpen={Db(d => d.Sessions.Count(s => s.Status != SessionStatus.Closed))}");
        foreach (var id in Db(d => d.Sessions.Where(s => s.Status != SessionStatus.Closed).Select(s => s.Id).ToList()))
            Log("  retry stop: " + await Show(await c.PostAsync($"/api/sessions/{id}/stop", null)));
        Log($"after retry: invoices={Db(d => d.Invoices.Count())} numbers={string.Join(",", Db(d => d.Invoices.OrderBy(i => i.Number).Select(i => i.Number).ToList()))}");
    }

    [Fact] public async Task P05_future_checkpoint_and_backdated_start()
    {
        await Reset(); Log("== P05 future checkpoint / client startedAt ==");
        await Reg("PC-F1", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await Start("PC-F1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        Log("checkpoint at 19:00 while now=14:10: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 19, 0) })));
        Log("stop at 14:10: " + await Show(await c.PostAsync($"/api/sessions/{id}/stop", null)));
        await Reg("PS5-B1", GameTimerReference.Ps5GroupId);
        var r = await c.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-B1", playerCount = 1, startedAt = TestTimes.T(2026, 6, 3, 4, 10) });
        Log("start with startedAt=04:10 (now 14:10): " + await Show(r));
        if (r.IsSuccessStatusCode)
        {
            var sid = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
            Log("stop after backdated start: " + await Show(await c.PostAsync($"/api/sessions/{sid}/stop", null)));
        }
    }

    [Fact] public async Task P06_station_id_rules()
    {
        await Reset(); Log("== P06 station ids ==");
        Log("register ' pc-٠١ ': " + await Show(await c.PostAsJsonAsync("/api/stations", new { id = " pc-٠١ ", name = "x", groupId = PilotSeed.PcNormalGroupId })));
        Log("register 'PC-01': " + await Show(await c.PostAsJsonAsync("/api/stations", new { id = "PC-01", name = "x", groupId = PilotSeed.PcNormalGroupId })));
        Log("register 'PC_01' (distinct id, ILIKE '_' wildcard): " + await Show(await c.PostAsJsonAsync("/api/stations", new { id = "PC_01", name = "x", groupId = PilotSeed.PcNormalGroupId })));
        Log("register '%' : " + await Show(await c.PostAsJsonAsync("/api/stations", new { id = "%", name = "x", groupId = PilotSeed.PcNormalGroupId })));
        Log("GET /api/stations/pc-۰۱: " + await Show(await c.GetAsync("/api/stations/pc-۰۱")));
        Log("stations: " + string.Join(",", Db(d => d.Stations.Select(s => s.Id).ToList())));
        Log("register unknown group: " + await Show(await c.PostAsJsonAsync("/api/stations", new { id = "PC-9", name = "x", groupId = "nope" })));
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => c.PostAsJsonAsync("/api/stations", new { id = "PC-RACE", name = "x", groupId = PilotSeed.PcNormalGroupId }))));
        Log("6 concurrent registers of PC-RACE: " + string.Join(",", rs.Select(r => (int)r.StatusCode)) + " stored=" + Db(d => d.Stations.Count(s => s.Id == "PC-RACE")));
    }

    [Fact] public async Task P07_audit_append_only()
    {
        await Reset(); Log("== P07 audit ==");
        await Reg("PC-AU", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var sql in new[] {
            "UPDATE audit_log SET action = 'tamper' WHERE id = 1",
            "UPDATE audit_log SET \"Action\" = 'tamper'",
            "DELETE FROM audit_log",
            "TRUNCATE audit_log" })
        {
            try { var n = await db.Database.ExecuteSqlRawAsync(sql); Log($"  {sql} -> OK rows={n}"); }
            catch (Exception e) { Log($"  {sql} -> {e.GetType().Name}: {e.Message.Split('\n')[0]}"); }
        }
        await Reg("PC-AU2", PilotSeed.PcNormalGroupId);
        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = db2.AuditLog.First();
        row.Action = "tamper-via-ef";
        try { await db2.SaveChangesAsync(); Log("  EF update -> saved"); } catch (Exception e) { Log($"  EF update -> {e.GetType().Name}: {(e.InnerException ?? e).Message.Split('\n')[0]}"); }
    }

    [Fact] public async Task P08_scenarios_independent()
    {
        await Reset(); Log("== P08 scenario re-checks ==");
        await Reg("PS5-S", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s11 = await Start("PS5-S");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Log("S11 live@14:30: " + await Show(await c.GetAsync($"/api/sessions/{s11}/live-amount")) + " status=" + Db(d => d.Sessions.Single(s => s.Id == s11).Status) + " invoices=" + Db(d => d.Invoices.Count()));
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        Log("S9 pause: " + await Show(await c.PostAsync($"/api/sessions/{s11}/pause", null)));
        Log("S9 pause again: " + await Show(await c.PostAsync($"/api/sessions/{s11}/pause", null)));
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        Log("S9 live while paused@14:50 (expect 55,000): " + await Show(await c.GetAsync($"/api/sessions/{s11}/live-amount")));
        Log("S9 resume: " + await Show(await c.PostAsync($"/api/sessions/{s11}/resume", null)));
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        Log("S9 stop@15:20: " + await Show(await c.PostAsync($"/api/sessions/{s11}/stop", null)));
        Log("stop again: " + await Show(await c.PostAsync($"/api/sessions/{s11}/stop", null)));
        await Reg("PS5-S2", GameTimerReference.Ps5GroupId);
        var s2 = await Start("PS5-S2");
        factory.Clock.SetTehran(2026, 6, 3, 15, 50);
        await c.PostAsync($"/api/sessions/{s2}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 16, 50);
        Log("stop while paused (15:20-15:50 played, 1p) expect 55,000: " + await Show(await c.PostAsync($"/api/sessions/{s2}/stop", null)));
        await Reg("PS5-S3", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 4, 2, 0);
        var s3 = await Start("PS5-S3");
        factory.Clock.SetTehran(2026, 6, 4, 3, 0);
        Log("PS5 1p 02:00-03:00 special expect 160,000: " + await Show(await c.PostAsync($"/api/sessions/{s3}/stop", null)));
        Log("player-count on PC → " + await Task.Run(async () => { await Reg("PC-PC", PilotSeed.PcNormalGroupId); var p = await Start("PC-PC"); return await Show(await c.PatchAsJsonAsync($"/api/sessions/{p}/player-count", new { playerCount = 2 })); }));
        Log("invoice numbers: " + string.Join(",", Db(d => d.Invoices.OrderBy(i => i.IssuedAtUtc).Select(i => i.Number).ToList())));
        Log("audit: " + string.Join(",", Db(d => d.AuditLog.OrderBy(a => a.Id).Select(a => a.Action).ToList())));
        Log("parts==gross check (unrounded+rounding==collectible): " + string.Join(",", Db(d => d.Invoices.Select(i => i.UnroundedPayableToman + i.RoundingToman == i.CollectibleToman).ToList())));
    }

    [Fact] public async Task P09_manual_settle_edges()
    {
        await Reset(); Log("== P09 manual settle edges ==");
        await Reg("PC-M", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await Start("PC-M");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        Log("rejected ckpt: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 14, 59) })));
        Log("live in queue: " + await Show(await c.GetAsync($"/api/sessions/{id}/live-amount")));
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        Log("sweep at +30m (must not auto-close queued): " + await Show(await c.PostAsync("/api/internal/dead-pc-sweep", null)) + " invoices=" + Db(d => d.Invoices.Count()));
        Log("endAt < checkpoint: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = TestTimes.T(2026, 6, 3, 14, 50) })));
        Log("endAt == now: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = TestTimes.T(2026, 6, 3, 15, 30) })));
        Log("second settle: " + await Show(await c.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { })));
        Log("audit: " + string.Join(",", Db(d => d.AuditLog.OrderBy(a => a.Id).Select(a => a.Action + ":" + a.DetailsJson).ToList())));
    }

    [Fact] public async Task P10_rate_change_mid_session()
    {
        await Reset(); Log("== P10 rate change mid-session ==");
        await Reg("PC-R", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await Start("PC-R");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await c.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000L, roundingDirection = "Up", lines = new[] { new { groupId = PilotSeed.PcNormalGroupId, playMode = "Offline", playerCount = (int?)1, isSpecialBand = false, hourlyToman = 120_000L } } });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        Log("stop (30m@80k + 30m@120k would be 100,000; whole-session reprice gives 120,000): " + await Show(await c.PostAsync($"/api/sessions/{id}/stop", null)));
    }
}

[Collection("server")]
public sealed class ReviewProbes2(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient c = factory.CreateClientWithKey();
    private void Log(string s) => File.AppendAllText(Path.Combine(Path.GetTempPath(), "pr4-probes-out.txt"), s + "\n");

    [Fact] public async Task P11_sweep_jam_by_unclosable_session()
    {
        using (var s = factory.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("TRUNCATE audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, stations RESTART IDENTITY CASCADE;");
        Log("== P11 dead-PC sweep with one unclosable session ==");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ids = new List<Guid>();
        foreach (var st in new[] { "PC-J0", "PC-J1", "PC-J2", "PC-J3" })
        {
            await c.PostAsJsonAsync("/api/stations", new { id = st, name = st, groupId = PilotSeed.PcNormalGroupId });
            var r = await c.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 });
            ids.Add((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
        }
        await c.PatchAsJsonAsync("/api/stations/PC-J0/group", new { groupId = PilotSeed.PcVipGroupId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        foreach (var id in ids) await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at = TestTimes.T(2026, 6, 3, 15, 0) });
        // make the broken session first in heap order for sure: touch the others so they move to the end
        using (var s = factory.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("UPDATE sessions SET \"ManualSettleReason\" = NULL WHERE \"StationId\" <> 'PC-J0'");
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        await Task.Delay(3000);
        using var sc = factory.Services.CreateScope();
        var db = sc.ServiceProvider.GetRequiredService<AppDbContext>();
        Log("statuses after 3 s of hosted sweeps: " + string.Join(",", db.Sessions.OrderBy(x => x.StationId).Select(x => x.StationId + "=" + x.Status).ToList()) + " invoices=" + db.Invoices.Count());
    }
}
