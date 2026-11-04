using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using GameClub.Server.Services;

namespace GameClub.Server.Tests;

// Independent re-review-1 probes. Expected values come from the spec (step2-server-scope.md), not from the code.
[Collection("server")]
public sealed class Rr1Probes(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient c = factory.CreateClientWithKey();
    private const string Out = "/tmp/pr4-probes/rr1/out-rr1.txt";
    private void Log(string s) => File.AppendAllText(Out, s + "\n");
    private void Check(string name, object? expected, object? actual) => Log($"  [{(Equals(expected, actual) ? "PASS" : "FAIL")}] {name}: expected={expected} actual={actual}");

    private async Task Reset()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("""
            TRUNCATE audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, rate_card_snapshot, stations RESTART IDENTITY CASCADE;
            SELECT setval('invoice_number_seq', 1, false);
            UPDATE rate_lines SET "HourlyToman" = 80000 WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false;
            UPDATE rate_lines SET "HourlyToman" = 110000 WHERE "GroupId" = 'pc-vip' AND "IsSpecialBand" = false;
            UPDATE rate_lines SET "HourlyToman" = 110000 WHERE "GroupId" = 'ps5' AND "IsSpecialBand" = false AND "PlayerCount" = 1;
            UPDATE rate_lines SET "HourlyToman" = 125000 WHERE "GroupId" = 'ps5' AND "IsSpecialBand" = false AND "PlayerCount" = 2;
            UPDATE branch_rounding SET "StepToman" = 5000, "Direction" = 'Up', "PcSpecialBandSurchargeToman" = 20000;
            """);
    }
    private T Db<T>(Func<AppDbContext, T> f) { using var s = factory.Services.CreateScope(); return f(s.ServiceProvider.GetRequiredService<AppDbContext>()); }
    private async Task Reg(string id, string g) => (await c.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = g })).EnsureSuccessStatusCode();
    private async Task<Guid> Start(string st, int p = 1)
    {
        var r = await c.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = p });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<(int code, JsonElement? body)> Read(HttpResponseMessage r)
    {
        var s = await r.Content.ReadAsStringAsync();
        try { return ((int)r.StatusCode, JsonDocument.Parse(s).RootElement.Clone()); } catch { return ((int)r.StatusCode, null); }
    }
    private async Task<long?> StopCollectible(Guid id)
    {
        var (code, b) = await Read(await c.PostAsync($"/api/sessions/{id}/stop", null));
        return code == 200 ? b!.Value.GetProperty("collectibleToman").GetInt64() : -code;
    }
    private Task<HttpResponseMessage> Ckpt(Guid id, DateTimeOffset? at) => at is null
        ? c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })
        : c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at });
    private Task<HttpResponseMessage> PutRate(string group, int? players, long toman, long step = 5000, string mode = "Offline") =>
        c.PutAsJsonAsync("/api/rates", new { roundingStepToman = step, roundingDirection = "Up", lines = new[] { new { groupId = group, playMode = mode, playerCount = players, isSpecialBand = false, hourlyToman = toman } } });

    [Fact] public async Task Q2_future_checkpoint_boundary()
    {
        await Reset(); Log("== Q2 checkpoint later than now+60s → 400, state unchanged (spec PO default 9) ==");
        await Reg("PC-Q2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var id = await Start("PC-Q2");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        (await Ckpt(id, null)).EnsureSuccessStatusCode(); // accepted 14:30
        var before = Db(d => (d.Sessions.Single(s => s.Id == id).LastCheckpointUtc, d.Checkpoints.Count(), d.AuditLog.Count(), d.Sessions.Single(s => s.Id == id).Status));
        factory.Clock.SetTehran(2026, 6, 3, 15, 0, 0);
        var r61 = await Ckpt(id, TestTimes.T(2026, 6, 3, 15, 1, 1));
        Check("at = now+61s → 400", 400, (int)r61.StatusCode);
        var after = Db(d => (d.Sessions.Single(s => s.Id == id).LastCheckpointUtc, d.Checkpoints.Count(), d.AuditLog.Count(), d.Sessions.Single(s => s.Id == id).Status));
        Check("state unchanged after 400 (lastCkpt, ckpt rows, audit rows, status)", before.ToString(), after.ToString());
        var r3h = await Ckpt(id, TestTimes.T(2026, 6, 3, 18, 0));
        Check("at = now+3h → 400", 400, (int)r3h.StatusCode);
        var r60 = await Ckpt(id, TestTimes.T(2026, 6, 3, 15, 1, 0));
        Check("at = exactly now+60s → accepted (not 'later than')", 204, (int)r60.StatusCode);
        Log("  info: LastCheckpointUtc recorded for at=now+60s: " + Db(d => d.Sessions.Single(s => s.Id == id).LastCheckpointUtc?.ToString("o")) + " (server now 11:30:00Z)");
        Check("session still Open (not manual-settle) after the 400s", SessionStatus.Open, Db(d => d.Sessions.Single(s => s.Id == id).Status));
        // spec S13 shape with ordinary network delay: PC reports 'at=15:00' but it arrives at 15:00:30
        await Reg("PC-LAT", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var lat = await Start("PC-LAT");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0, 30);
        Log("  late report at=15:00 received 15:00:30: " + (int)(await Ckpt(lat, TestTimes.T(2026, 6, 3, 15, 0))).StatusCode);
        Log("  info: recorded last checkpoint = " + Db(d => d.Sessions.Single(s => s.Id == lat).LastCheckpointUtc?.ToString("o")) + " (reported 11:30:00Z)");
        Check("bill to last checkpoint (reported 15:00) → 80,000 [spec item 7/S13]", 80_000L, await StopCollectible(lat));
        // a buffered report from 14:40 delivered at 15:00 (≥ previous) is billed to 15:00?
        await Reg("PC-BUF", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var buf = await Start("PC-BUF");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        (await Ckpt(buf, TestTimes.T(2026, 6, 3, 14, 40))).EnsureSuccessStatusCode();
        Check("buffered report at=14:40 delivered 15:00, then stop → bill to 14:40 = 53,333 → 55,000", 55_000L, await StopCollectible(buf));
    }

    [Fact] public async Task Q3_server_clock_start_only()
    {
        await Reset(); Log("== Q3 start time is server clock only (spec PO default 10; user bar: client startedAt → 400) ==");
        await Reg("PS5-Q3", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        foreach (var (label, json) in new[] {
            ("startedAt", """{"stationId":"PS5-Q3","playerCount":1,"startedAt":"2026-06-03T04:00:00+03:30"}"""),
            ("StartedAt (Pascal)", """{"stationId":"PS5-Q3","playerCount":1,"StartedAt":"2026-06-03T04:00:00+03:30"}"""),
            ("STARTEDAT", """{"stationId":"PS5-Q3","playerCount":1,"STARTEDAT":"2026-06-03T04:00:00+03:30"}"""),
            ("startedAt future", """{"stationId":"PS5-Q3","playerCount":1,"startedAt":"2026-06-03T20:00:00+03:30"}"""),
            ("startedAt = now", """{"stationId":"PS5-Q3","playerCount":1,"startedAt":"2026-06-03T14:00:00+03:30"}"""),
        })
        {
            var r = await c.PostAsync("/api/sessions", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            Check($"POST /api/sessions with {label} → 400", 400, (int)r.StatusCode);
        }
        Check("no session created by those requests", 0, Db(d => d.Sessions.Count()));
        // other names that might be bound to a start time
        foreach (var (label, json) in new[] {
            ("startedAtUtc", """{"stationId":"PS5-Q3","playerCount":1,"startedAtUtc":"2026-06-03T00:30:00Z"}"""),
            ("startUtc", """{"stationId":"PS5-Q3","playerCount":1,"startUtc":"2026-06-03T00:30:00Z","start":"2026-06-03T04:00:00+03:30"}"""),
        })
        {
            var r = await c.PostAsync("/api/sessions", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            var (code, b) = await Read(r);
            if (code == 201)
            {
                var sid = b!.Value.GetProperty("id").GetGuid();
                Check($"{label}: ignored, start = server 14:00", TimeMapping.ToUtc(TestTimes.T(2026, 6, 3, 14, 0)), Db(d => d.Sessions.Single(s => s.Id == sid).StartedAtUtc));
                factory.Clock.SetTehran(2026, 6, 3, 15, 0);
                Check($"{label}: stop 14:00–15:00 → 110,000", 110_000L, await StopCollectible(sid));
                factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            }
            else Log($"  {label}: {code}");
        }
        var q = await c.PostAsJsonAsync("/api/sessions?startedAt=2026-06-03T04:00:00%2B03:30", new { stationId = "PS5-Q3", playerCount = 1 });
        var (qc, qb) = await Read(q);
        if (qc == 201) Check("query-string startedAt ignored", TimeMapping.ToUtc(TestTimes.T(2026, 6, 3, 14, 0)), Db(d => d.Sessions.Single(s => s.Id == qb!.Value.GetProperty("id").GetGuid()).StartedAtUtc));
        else Log("  query-string startedAt → " + qc);
        var open = Db(d => d.Sessions.Where(s => s.Status != SessionStatus.Closed).Select(s => s.Id).ToList()).FirstOrDefault();
        if (open != Guid.Empty)
        {
            foreach (var (verb, url, body) in new[] {
                ("PATCH", $"/api/sessions/{open}", """{"startedAt":"2026-06-03T04:00:00+03:30"}"""),
                ("PUT", $"/api/sessions/{open}", """{"startedAt":"2026-06-03T04:00:00+03:30"}"""),
                ("PATCH", $"/api/sessions/{open}/player-count", """{"playerCount":2,"startedAt":"2026-06-03T04:00:00+03:30","at":"2026-06-03T04:00:00+03:30"}"""),
                ("POST", $"/api/sessions/{open}/resume", """{"at":"2026-06-03T04:00:00+03:30"}"""),
            })
            {
                var r = await c.SendAsync(new HttpRequestMessage(new HttpMethod(verb), url) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
                Log($"  {verb} {url.Replace(open.ToString(), "{id}")} → {(int)r.StatusCode}");
            }
            Check("start of the open session unchanged by other endpoints", TimeMapping.ToUtc(TestTimes.T(2026, 6, 3, 14, 0)), Db(d => d.Sessions.Single(s => s.Id == open).StartedAtUtc));
            Check("segment start unchanged", TimeMapping.ToUtc(TestTimes.T(2026, 6, 3, 14, 0)), Db(d => d.Segments.Where(s => s.SessionId == open).Min(s => s.StartUtc)));
        }
        var openapiPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "GameClub.Server", "openapi.json"));
        Log("  info: openapi StartRequest still documents startedAt: " + File.ReadAllText(openapiPath).Contains("startedAt"));
    }

    [Fact] public async Task Q4_rates_locked_at_start()
    {
        await Reset(); Log("== Q4 rates locked at session start (spec PO default 11) ==");
        // stop path
        await Reg("PC-Q4A", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var a = await Start("PC-Q4A");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        (await PutRate(PilotSeed.PcNormalGroupId, 1, 120_000)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await Ckpt(a, null);
        Check("PC stop after mid-session rate change → start rate 80,000", 80_000L, await StopCollectible(a));
        var n = await Start("PC-Q4A");
        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        await Ckpt(n, null);
        Check("new session after the change → new rate 120,000", 120_000L, await StopCollectible(n));

        // PS5 pause/resume + live amount
        await Reset();
        await Reg("PS5-Q4", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var p = await Start("PS5-Q4");
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        (await PutRate(GameTimerReference.Ps5GroupId, 1, 200_000)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var live = (await c.GetFromJsonAsync<JsonElement>($"/api/sessions/{p}/live-amount")).GetProperty("amountToman").GetInt64();
        Check("live amount at 14:30 → 55,000 at start rate", 55_000L, live);
        await c.PostAsync($"/api/sessions/{p}/pause", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        await c.PostAsync($"/api/sessions/{p}/resume", null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        Check("S9 shape with rate change at 14:10 → 110,000", 110_000L, await StopCollectible(p));

        // dead-PC sweep path
        await Reset();
        await Reg("PC-Q4S", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var s = await Start("PC-Q4S");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        (await PutRate(PilotSeed.PcNormalGroupId, 1, 120_000)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await Ckpt(s, null);
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        await c.PostAsync("/api/internal/dead-pc-sweep", null);
        Check("dead-PC auto-close after mid-session rate change → 80,000", 80_000L, Db(d => d.Invoices.Where(i => i.SessionId == s).Select(i => (long?)i.CollectibleToman).SingleOrDefault()));

        // manual-settle path
        await Reset();
        await Reg("PC-Q4M", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var m = await Start("PC-Q4M");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await Ckpt(m, null);
        (await PutRate(PilotSeed.PcNormalGroupId, 1, 120_000)).EnsureSuccessStatusCode();
        await Ckpt(m, TestTimes.T(2026, 6, 3, 14, 30)); // rejected → queue
        var ms = await Read(await c.PostAsJsonAsync($"/api/sessions/{m}/manual-settle", new { }));
        Check("manual settle after rate change → 80,000", 80_000L, ms.body?.GetProperty("collectibleToman").GetInt64());

        // CN-3 controller change after rate change
        await Reset();
        await Reg("PS5-CN", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var cn = await Start("PS5-CN", 2);
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await c.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000L, roundingDirection = "Up", lines = new[] {
            new { groupId = "ps5", playMode = "Offline", playerCount = (int?)1, isSpecialBand = false, hourlyToman = 200_000L },
            new { groupId = "ps5", playMode = "Offline", playerCount = (int?)2, isSpecialBand = false, hourlyToman = 200_000L } } });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await c.PatchAsJsonAsync($"/api/sessions/{cn}/player-count", new { playerCount = 1 });
        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        Check("CN-3 (2p 1h + 1p 1h) after rate change → 125,000+110,000 = 235,000", 235_000L, await StopCollectible(cn));

        // surcharge + rounding step changed mid-session (info: 'rates' locked; is rounding locked too?)
        await Reset();
        await Reg("PC-Q4R", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 8, 0);
        var rr = await Start("PC-Q4R");
        factory.Clock.SetTehran(2026, 6, 3, 8, 10);
        await c.PutAsJsonAsync("/api/rates", new { roundingStepToman = 10_000L, roundingDirection = "Up", pcSpecialBandSurchargeToman = 50_000L, lines = Array.Empty<object>() });
        factory.Clock.SetTehran(2026, 6, 3, 8, 50);
        await Ckpt(rr, null);
        var (rc, rb) = await Read(await c.PostAsync($"/api/sessions/{rr}/stop", null));
        Check("S5-shape 08:00–08:50 with surcharge→50k mid-session: band surcharge locked → 50,000+26,667=76,667 unrounded", 76_667L, rb?.GetProperty("unroundedPayableToman").GetInt64());
        Log("  info: collectible with rounding step changed 5,000→10,000 mid-session: " + rb?.GetProperty("collectibleToman").GetInt64() + " (80,000 = start step locked; 80,000 also under 10k) ");
    }

    [Fact] public async Task C1_concurrency()
    {
        await Reset(); Log("== C1 concurrency ==");
        // start vs start, 20 rounds
        await Reg("PS5-CC", GameTimerReference.Ps5GroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var maxOpen = 0; var bad = 0;
        for (var round = 0; round < 20; round++)
        {
            var rs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => c.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-CC", playerCount = 1 }))));
            var codes = rs.Select(r => (int)r.StatusCode).ToList();
            if (codes.Count(x => x == 201) != 1 || codes.Any(x => x is not (201 or 409))) bad++;
            maxOpen = Math.Max(maxOpen, Db(d => d.Sessions.Count(s => s.StationId == "PS5-CC" && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused))));
            foreach (var id in Db(d => d.Sessions.Where(s => s.Status != SessionStatus.Closed).Select(s => s.Id).ToList())) await c.PostAsync($"/api/sessions/{id}/stop", null);
        }
        Check("start vs start (20×10): max open sessions on one station", 1, maxOpen);
        Check("start vs start: rounds not exactly one 201 + rest 409", 0, bad);

        // start while Paused → 409
        var pz = await Start("PS5-CC");
        await c.PostAsync($"/api/sessions/{pz}/pause", null);
        Check("start while the station's session is Paused → 409", 409, (int)(await c.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-CC", playerCount = 1 })).StatusCode);
        await c.PostAsync($"/api/sessions/{pz}/stop", null);

        // start vs group change race (TOCTOU)
        var poisoned = 0; var both = 0; var rounds = 150;
        for (var i = 0; i < rounds; i++)
        {
            var st = $"PC-T{i}";
            await Reg(st, PilotSeed.PcNormalGroupId);
            using var gate = new ManualResetEventSlim(false);
            var tStart = Task.Run(async () => { gate.Wait(); return await c.PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 }); });
            var tGroup = Task.Run(async () => { gate.Wait(); return await c.PatchAsJsonAsync($"/api/stations/{st}/group", new { groupId = PilotSeed.PcVipGroupId }); });
            gate.Set();
            var r = await Task.WhenAll(tStart, tGroup);
            if (r[0].StatusCode == HttpStatusCode.Created && r[1].IsSuccessStatusCode) both++;
            var segGroup = Db(d => d.Segments.Where(s => s.StationId == st).Select(s => s.GroupId).FirstOrDefault());
            var dirGroup = Db(d => d.Stations.Single(s => s.Id == st).GroupId);
            if (segGroup is not null && segGroup != dirGroup) poisoned++;
        }
        Check($"start vs group change ({rounds} races): sessions whose segment group != directory group", 0, poisoned);
        Log($"  info: rounds where both start=201 and group change=2xx: {both}");
        if (poisoned > 0)
        {
            var sid = Db(d => d.Sessions.Join(d.Segments, s => s.Id, g => g.SessionId, (s, g) => new { s, g }).Join(d.Stations, x => x.s.StationId, t => t.Id, (x, t) => new { x.s.Id, sg = x.g.GroupId, tg = t.GroupId }).Where(x => x.sg != x.tg).Select(x => x.Id).First());
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            try { Log("  poisoned session: checkpoint → " + (int)(await Ckpt(sid, null)).StatusCode + ", stop → " + (int)(await c.PostAsync($"/api/sessions/{sid}/stop", null)).StatusCode + ", live → " + (int)(await c.GetAsync($"/api/sessions/{sid}/live-amount")).StatusCode); }
            catch (Exception e) { Log("  poisoned session request threw " + e.GetType().Name + ": " + e.Message.Split('\n')[0]); }
        }

        // start while a manual-settle session exists on the station
        await Reset();
        await Reg("PC-MS", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ms = await Start("PC-MS");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await Ckpt(ms, null);
        await Ckpt(ms, TestTimes.T(2026, 6, 3, 14, 30));
        Check("first session is in manual-settle queue", SessionStatus.ManualSettle, Db(d => d.Sessions.Single(s => s.Id == ms).Status));
        var rs2 = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => c.PostAsJsonAsync("/api/sessions", new { stationId = "PC-MS", playerCount = 1 }))));
        Check("10 concurrent starts with a queued session: exactly one 201", 1, rs2.Count(r => r.StatusCode == HttpStatusCode.Created));
        Check("open/paused sessions on PC-MS", 1, Db(d => d.Sessions.Count(s => s.StationId == "PC-MS" && (s.Status == SessionStatus.Open || s.Status == SessionStatus.Paused))));
        Check("group change still blocked while queued + open sessions exist", 409, (int)(await c.PatchAsJsonAsync("/api/stations/PC-MS/group", new { groupId = PilotSeed.PcVipGroupId })).StatusCode);
        var settle = await Read(await c.PostAsJsonAsync($"/api/sessions/{ms}/manual-settle", new { }));
        Check("queued session settles to last accepted checkpoint 15:00 → 80,000", 80_000L, settle.body?.GetProperty("collectibleToman").GetInt64());
        var newId = Db(d => d.Sessions.Single(s => s.StationId == "PC-MS" && s.Status == SessionStatus.Open).Id);
        factory.Clock.SetTehran(2026, 6, 3, 16, 0);
        await Ckpt(newId, null);
        Check("second session 15:00–16:00 → 80,000", 80_000L, await StopCollectible(newId));
        Log("  info: index def: " + Db(d => d.Database.SqlQueryRaw<string>("select indexdef as \"Value\" from pg_indexes where indexname = 'ux_sessions_station_active'").ToList().FirstOrDefault()));

        // concurrent sweep vs operator stop on the same dead PC: one invoice, and numbering gaps
        await Reset();
        var gapsRounds = 10; var dup = 0;
        for (var i = 0; i < gapsRounds; i++)
        {
            var st = $"PC-G{i}";
            await Reg(st, PilotSeed.PcNormalGroupId);
            factory.Clock.SetTehran(2026, 6, 3, 14, 0);
            var id = await Start(st);
            factory.Clock.SetTehran(2026, 6, 3, 15, 0);
            await Ckpt(id, null);
            factory.Clock.SetTehran(2026, 6, 3, 15, 10);
            await Task.WhenAll(Task.Run(() => c.PostAsync("/api/internal/dead-pc-sweep", null)), Task.Run(() => c.PostAsync($"/api/sessions/{id}/stop", null)), Task.Run(() => c.PostAsync($"/api/sessions/{id}/stop", null)));
            if (Db(d => d.Invoices.Count(x => x.SessionId == id)) != 1) dup++;
        }
        Check("sweep vs stop races: sessions without exactly one invoice", 0, dup);
        var nums = Db(d => d.Invoices.OrderBy(i => i.Number).Select(i => i.Number).ToList());
        Log($"  info: invoice numbers after races: {string.Join(",", nums)} (gaps = {nums.Last() - nums.First() + 1 - nums.Count})");
    }

    [Fact] public async Task S1_sweep_continues_past_failing_session()
    {
        await Reset(); Log("== S1 sweep continues when one close fails ==");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var ids = new List<Guid>();
        foreach (var st in new[] { "PC-K0", "PC-K1", "PC-K2", "PC-K3" }) { await Reg(st, PilotSeed.PcNormalGroupId); ids.Add(await Start(st)); }
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        foreach (var id in ids) await Ckpt(id, null);
        // poison K0 and K2 directly in the DB (the API now refuses a busy group change)
        using (var s = factory.Services.CreateScope())
            await s.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync("UPDATE stations SET \"GroupId\" = 'pc-vip' WHERE \"Id\" IN ('PC-K0','PC-K2')");
        factory.Clock.SetTehran(2026, 6, 3, 15, 10);
        var (code, body) = await Read(await c.PostAsync("/api/internal/dead-pc-sweep", null));
        Check("manual sweep HTTP status", 200, code);
        Check("manual sweep closed count", 2, body?.GetProperty("closed").GetInt32());
        await Task.Delay(2500);
        Check("healthy K1,K3 closed with 80,000 each", "PC-K1=80000,PC-K3=80000", string.Join(",", Db(d => d.Invoices.Join(d.Sessions, i => i.SessionId, x => x.Id, (i, x) => new { x.StationId, i.CollectibleToman }).OrderBy(x => x.StationId).Select(x => x.StationId + "=" + x.CollectibleToman).ToList())));
        Log("  info: poisoned statuses: " + string.Join(",", Db(d => d.Sessions.Where(x => x.StationId == "PC-K0" || x.StationId == "PC-K2").Select(x => x.StationId + "=" + x.Status).ToList())));
        Log("  info: operator stop on poisoned K0 → " + (int)(await c.PostAsync($"/api/sessions/{ids[0]}/stop", null)).StatusCode);
    }
}
