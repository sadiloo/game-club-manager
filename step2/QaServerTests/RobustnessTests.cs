using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace Qa.Step2;

/// Clarifications PO-C1..C7, conservation, auth/validation, idempotency/concurrency, server time, station ids.
[Collection("qa")]
public sealed class RobustnessTests(MainFactory f) : QaBase(f)
{
    // ---- PO-C1: PC +20k, PS5 per special table ----
    [Theory]
    [InlineData(1, "Offline", 160_000)]
    [InlineData(2, "Offline", 175_000)]
    [InlineData(3, "Offline", 210_000)]
    [InlineData(4, "Offline", 270_000)]
    [InlineData(1, "Online", 220_000)]
    public async Task QA_C1_ps5_special_band_02_03_per_table(int players, string mode, long expected)
    {
        await F.ResetAsync(); await Reg("PS5-S", Ps5); F.At(2, 0);
        var id = await Start("PS5-S", players, mode); F.At(3, 0); Inv(await Stop(id), expected, expected);
    }
    [Fact] public async Task QA_C1_pc_normal_02_03_is_100000()
    { await F.ResetAsync(); await Reg("PC-S", Normal); Inv(await PcRun("PC-S", (2, 0), (3, 0)), 100_000, 100_000); }
    [Fact] public async Task QA_C1_ps5_1p_07_30_09_30_is_270000()
    { await F.ResetAsync(); await Reg("PS5-X", Ps5); F.At(7, 30); var id = await Start("PS5-X", 1); F.At(9, 30); Inv(await Stop(id), 270_000, 270_000); }
    [Fact] public async Task QA_C1_pc_normal_23_30_00_30_is_90000()
    { await F.ResetAsync(); await Reg("PC-M", Normal); Inv(await PcRun("PC-M", (23, 30), (0, 30)), 90_000, 90_000); }
    [Theory]
    [InlineData(1, 110_000)] [InlineData(4, 220_000)]
    public async Task QA_C1_ps5_daytime_rates(int players, long expected)
    { await F.ResetAsync(); await Reg("PS5-D", Ps5); F.At(14, 0); var id = await Start("PS5-D", players); F.At(15, 0); Inv(await Stop(id), expected, expected); }

    // ---- PO-C5: manual settle (b) operator end time, audited; bounds ----
    [Fact] public async Task QA_C5_manual_settle_default_last_checkpoint_audited_and_once_only()
    {
        await F.ResetAsync(); await Reg("PC-MS", Normal);
        F.At(14, 0); var id = await Start("PC-MS"); F.At(15, 0); await Cp(id);
        F.At(15, 1); await CpAt(id, QaFactory.T(14, 30));
        F.At(15, 30);
        var r = await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); Inv(await r.Json(), 80_000, 80_000);
        Assert.Contains("session.manual_settle_resolve", await AuditActions());
        var again = await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(1, await F.Scalar("select count(*) from invoices"));
    }
    [Fact] public async Task QA_C5_manual_settle_operator_end_time_bounds()
    {
        await F.ResetAsync(); await Reg("PC-MT", Normal);
        F.At(14, 0); var id = await Start("PC-MT"); F.At(15, 0); await Cp(id);
        F.At(15, 1); await CpAt(id, QaFactory.T(14, 30)); F.At(15, 30);
        Assert.Equal(HttpStatusCode.BadRequest, (await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = QaFactory.T(15, 31) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = QaFactory.T(14, 59) })).StatusCode);
        Assert.Equal(0, await F.Scalar("select count(*) from invoices"));
        var ok = await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = QaFactory.T(15, 30) }); // exactly now allowed
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode); Inv(await ok.Json(), 120_000, 120_000);
        var d = await F.Text("select \"DetailsJson\" from audit_log where \"Action\"='session.manual_settle_resolve'");
        Assert.NotNull(d);
    }
    [Fact] public async Task QA_C5_manual_settle_on_open_session_409()
    {
        await F.ResetAsync(); await Reg("PC-MO", Normal); F.At(14, 0); var id = await Start("PC-MO");
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { })).StatusCode);
    }

    // ---- PO-C6: live started minute, unrounded ----
    [Fact] public async Task QA_C6_live_65s_is_3667_not_rounded()
    { await F.ResetAsync(); await Reg("PS5-L", Ps5); F.At(14, 0, 0); var id = await Start("PS5-L"); F.At(14, 1, 5); Assert.Equal(3_667, await Live(id)); }

    // ---- PO-C7: openapi documents every route the server maps ----
    [Fact] public async Task QA_C7_served_openapi_matches_committed_file_and_documents_api_key()
    {
        var anon = F.Api(null);
        var served = JsonDocument.Parse(await anon.GetStringAsync("/swagger/v1/swagger.json")).RootElement;
        var committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("PR4_ROOT") ?? "/workspace/qa/regress-pr4-b5de45f/src-tree", "src/GameClub.Server/openapi.json"))).RootElement;
        var a = served.GetProperty("paths").EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        var b = committed.GetProperty("paths").EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToList();
        Assert.Equal(b, a);
        // header documented as a security scheme (PR body: X-Api-Key)
        Assert.True(committed.TryGetProperty("components", out var comp) && comp.TryGetProperty("securitySchemes", out _), "openapi.json has no securitySchemes for X-Api-Key");
    }

    // ---- Money conservation ----
    [Fact] public async Task QA_M1_conservation_over_mixed_invoices()
    {
        await F.ResetAsync();
        await Reg("PC-1", Normal); await Reg("PC-2", Vip); await Reg("PS5-1", Ps5);
        await PcRun("PC-1", (14, 0), (14, 50)); await PcRun("PC-2", (8, 13), (8, 47));
        F.At(14, 0, 0); var s = await Start("PS5-1", 3); F.At(14, 7, 31); await Stop(s);
        F.At(15, 0); s = await Start("PS5-1", 2); F.At(15, 0, 20); await Stop(s);
        Assert.Equal(0, await F.Scalar("select count(*) from invoices where \"UnroundedPayableToman\"<0 or \"RoundingToman\"<0 or \"CollectibleToman\"<0"));
        Assert.Equal(0, await F.Scalar("select count(*) from invoices where \"UnroundedPayableToman\"+\"RoundingToman\"<>\"CollectibleToman\""));
        Assert.Equal(0, await F.Scalar("select count(*) from invoices where \"CollectibleToman\" % 5000 <> 0 or \"RoundingToman\">=5000"));
        Assert.Equal(4, await F.Scalar("select count(distinct \"Number\") from invoices"));
        Assert.Equal(4, await F.Scalar("select max(\"Number\") from invoices")); // sequential 1..n
        Assert.Equal(0, await F.Scalar("select count(*) from play_segments where \"EndUtc\" < \"StartUtc\""));
    }
    [Fact] public async Task QA_M2_zero_minute_pc_stop_does_not_create_negative_or_bogus_rounding()
    {
        await F.ResetAsync(); await Reg("PC-Z", Normal); F.At(14, 0); var id = await Start("PC-Z");
        var inv = await Stop(id); Inv(inv, 0, 0);
    }

    // ---- Auth ----
    [Fact] public async Task QA_A1_api_key_missing_wrong_or_empty_is_401_everywhere()
    {
        foreach (var key in new string?[] { null, "wrong", "", "QA-KEY", "qa-key " })
        {
            var c = F.Api(key);
            foreach (var (m, u) in new[] { (HttpMethod.Get, "/api/stations"), (HttpMethod.Post, "/api/stations"), (HttpMethod.Get, "/api/audit"), (HttpMethod.Put, "/api/rates"), (HttpMethod.Post, "/api/internal/dead-pc-sweep"), (HttpMethod.Post, "/api/sessions") })
                Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(new HttpRequestMessage(m, u) { Content = JsonContent.Create(new { }) })).StatusCode);
        }
        var lower = F.CreateClient(); lower.DefaultRequestHeaders.Add("x-api-key", "qa-key");
        Assert.Equal(HttpStatusCode.OK, (await lower.GetAsync("/api/stations")).StatusCode);
    }

    // ---- Validation ----
    [Fact] public async Task QA_V1_validation_errors_are_4xx_never_500()
    {
        await F.ResetAsync(); await Reg("PC-V", Normal); await Reg("PS5-V", Ps5); await Reg("PS5-W", Ps5);
        var bad = new List<string>();
        async Task Expect(HttpResponseMessage r, int code, string what) { if ((int)r.StatusCode != code) bad.Add($"{what}: expected {code}, got {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}"); }
        await Expect(await C.PostAsJsonAsync("/api/stations", new { id = "   ", name = "x", groupId = Normal }), 400, "blank id");
        await Expect(await C.PostAsJsonAsync("/api/stations", new { id = "PC-Q", name = "x", groupId = "nope" }), 400, "unknown group");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-V", playerCount = 0 }), 400, "players 0");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-V", playerCount = 5 }), 400, "players 5");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = "nope", playerCount = 1 }), 404, "unknown station");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-W", playerCount = 1, mode = "Turbo" }), 400, "unknown mode");
        await Expect(await C.PostAsync("/api/sessions", new StringContent("{bad", System.Text.Encoding.UTF8, "application/json")), 400, "malformed json");
        await Expect(await C.PostAsync($"/api/sessions/{Guid.NewGuid()}/stop", null), 404, "unknown session");
        F.At(14, 0); var pc = await Start("PC-V"); var ps = await Start("PS5-V");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = "pc-v", playerCount = 1 }), 409, "station busy");
        await Expect(await CpAt(ps, QaFactory.T(14, 1)), 400, "checkpoint on console");
        await Expect(await C.Patch($"/api/sessions/{pc}/player-count", new { playerCount = 2 }), 400, "player-count on PC");
        await Expect(await C.Patch($"/api/sessions/{ps}/player-count", new { playerCount = 9 }), 400, "player-count 9");
        await Expect(await C.PostAsync($"/api/sessions/{ps}/resume", null), 409, "resume not paused");
        await Expect(await C.PostAsJsonAsync("/api/stations", new { id = "PC-NONAME", groupId = Normal }), 400, "missing name");
        await Expect(await C.Patch("/api/stations/PC-V/group", new { groupId = "nope" }), 400, "group patch unknown");
        await Expect(await C.PostAsJsonAsync("/api/sessions", new { stationId = (string?)null, playerCount = 1 }), 400, "null station id");
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
    [Fact] public async Task QA_I7_concurrent_pause_then_resume_bills_all_unpaused_minutes()
    {
        await F.ResetAsync(); await Reg("PS5-P2", Ps5); F.At(14, 0); var id = await Start("PS5-P2"); F.At(14, 10);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/pause", null)));
        F.At(14, 20); await C.PostAsync($"/api/sessions/{id}/resume", null);
        F.At(15, 0);
        var inv = await Stop(id);
        Assert.True(inv.L("unroundedPayableToman") == 91_667, $"50 unpaused min × 110,000/60 = 91,667; billed {inv.L("unroundedPayableToman")}");
    }
    [Fact] public async Task QA_I8_concurrent_start_same_station_double_billing()
    {
        await F.ResetAsync(); await Reg("PS5-R2", Ps5); F.At(14, 0);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => F.Api().PostAsJsonAsync("/api/sessions", new { stationId = "PS5-R2", playerCount = 1 })));
        F.At(15, 0);
        var ids = new List<Guid>();
        await using (var c = new Npgsql.NpgsqlConnection(F.Conn)) { await c.OpenAsync(); await using var rd = await new Npgsql.NpgsqlCommand("select \"Id\" from sessions", c).ExecuteReaderAsync(); while (await rd.ReadAsync()) ids.Add(rd.GetGuid(0)); }
        long total = 0; foreach (var i in ids) total += (await Stop(i)).L("unroundedPayableToman");
        Assert.True(total == 110_000, $"one PS5 hour must bill 110,000 once; {ids.Count} sessions billed {total}");
    }
    [Fact] public async Task QA_V2_rate_update_rejects_negative_rate_and_bad_rounding()
    {
        await F.ResetAsync();
        var rates = await C.GetFromJsonAsync<JsonElement>("/api/rates");
        var line = rates.GetProperty("lines").EnumerateArray().First(l => l.GetProperty("groupId").GetString() == Normal && !l.GetProperty("isSpecialBand").GetBoolean());
        var neg = new { roundingStepToman = 5000, roundingDirection = "Up", lines = new[] { new { groupId = Normal, playMode = line.GetProperty("playMode").GetString(), playerCount = (int?)1, isSpecialBand = false, hourlyToman = -80_000L } } };
        var r1 = await C.PutAsJsonAsync("/api/rates", neg);
        var zero = new { roundingStepToman = 0, roundingDirection = "Up", lines = Array.Empty<object>() };
        var r2 = await C.PutAsJsonAsync("/api/rates", zero);
        var dir = new { roundingStepToman = 5000, roundingDirection = "Sideways", lines = Array.Empty<object>() };
        var r3 = await C.PutAsJsonAsync("/api/rates", dir);
        var stored = await C.GetFromJsonAsync<JsonElement>("/api/rates");
        await F.ResetAsync();
        var codes = $"negative rate {(int)r1.StatusCode}, step 0 {(int)r2.StatusCode}, direction Sideways {(int)r3.StatusCode}";
        Assert.True(r1.StatusCode == HttpStatusCode.BadRequest && r2.StatusCode == HttpStatusCode.BadRequest && r3.StatusCode == HttpStatusCode.BadRequest, codes);
        Assert.Equal(5000, stored.L("roundingStepToman"));
    }

    // ---- Idempotency / double collect / concurrency ----
    [Fact] public async Task QA_I1_double_stop_second_is_409_one_invoice()
    {
        await F.ResetAsync(); await Reg("PS5-I", Ps5); F.At(14, 0); var id = await Start("PS5-I"); F.At(15, 0);
        await Stop(id);
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
        Assert.Equal(1, await F.Scalar("select count(*) from invoices"));
        Assert.Equal(1, await F.Scalar("select count(*) from audit_log where \"Action\"='session.stop'"));
    }
    [Fact] public async Task QA_I2_concurrent_stop_same_session_one_200_rest_409_no_500()
    {
        await F.ResetAsync(); await Reg("PS5-C", Ps5); F.At(14, 0); var id = await Start("PS5-C"); F.At(15, 0);
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/stop", null)));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, await F.Scalar("select count(*) from invoices"));
        Assert.Equal(1, codes.Count(c => c == 200));
        Assert.True(codes.All(c => c is 200 or 409), "codes: " + string.Join(",", codes));
        Assert.Equal(1, await F.Scalar("select count(*) from audit_log where \"Action\"='session.stop'"));
    }
    [Fact] public async Task QA_I3_concurrent_stop_different_sessions_all_succeed_unique_sequential_numbers()
    {
        await F.ResetAsync(); F.At(14, 0);
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++) { await Reg($"PS5-{i}", Ps5); ids.Add(await Start($"PS5-{i}")); }
        F.At(15, 0);
        var rs = await Task.WhenAll(ids.Select(id => F.Api().PostAsync($"/api/sessions/{id}/stop", null)));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.True(codes.All(c => c == 200), "codes: " + string.Join(",", codes));
        Assert.Equal(6, await F.Scalar("select count(distinct \"Number\") from invoices"));
        Assert.Equal(6, await F.Scalar("select max(\"Number\") from invoices"));
    }
    [Fact] public async Task QA_I4_concurrent_start_same_station_one_session()
    {
        await F.ResetAsync(); await Reg("PS5-R", Ps5); F.At(14, 0);
        var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => F.Api().PostAsJsonAsync("/api/sessions", new { stationId = "PS5-R", playerCount = 1 })));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, await F.Scalar("select count(*) from sessions where \"Status\" in ('Open','Paused')"));
        Assert.Equal(1, codes.Count(c => c == 201));
        Assert.True(codes.All(c => c is 201 or 409), "codes: " + string.Join(",", codes));
    }
    [Fact] public async Task QA_I5_concurrent_duplicate_register_after_normalising_one_row_rest_409()
    {
        await F.ResetAsync();
        var ids = new[] { " pc-۰9 ", "PC-09", "pc-٠٩", "PC-０９", "pc-09", "Pc-09", " PC-09", "pc-09 " };
        var rs = await Task.WhenAll(ids.Select(i => F.Api().PostAsJsonAsync("/api/stations", new { id = i, name = i, groupId = Normal })));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, await F.Scalar("select count(*) from stations"));
        Assert.Equal(1, codes.Count(c => c == 201));
        Assert.True(codes.All(c => c is 201 or 409), "codes: " + string.Join(",", codes));
    }
    [Fact] public async Task QA_I6_concurrent_pause_one_pause_interval()
    {
        await F.ResetAsync(); await Reg("PS5-P", Ps5); F.At(14, 0); var id = await Start("PS5-P"); F.At(14, 10);
        var rs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/pause", null)));
        var codes = rs.Select(r => (int)r.StatusCode).ToList();
        Assert.Equal(1, await F.Scalar("select count(*) from pause_intervals"));
        Assert.True(codes.All(c => c is 204 or 409), "codes: " + string.Join(",", codes));
    }

    // ---- Server time authoritative ----
    [Fact] public async Task QA_T1_client_started_at_is_rejected_and_server_time_is_used()
    {
        await F.ResetAsync(); await Reg("PS5-T", Ps5); F.At(14, 0);
        var bad = await C.PostAsJsonAsync("/api/sessions", new { stationId = "PS5-T", playerCount = 1, startedAt = QaFactory.T(12, 0) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var id = await Start("PS5-T");
        F.At(15, 0);
        var inv = await Stop(id);
        Assert.True(inv.L("unroundedPayableToman") == 110_000, $"server 14:00–15:00 must bill 110,000; got {inv.L("unroundedPayableToman")}");
    }
    [Fact] public async Task QA_T2_future_client_checkpoint_at_returns_400_and_billing_uses_last_accepted()
    {
        await F.ResetAsync(); await Reg("PC-T", Normal); F.At(14, 0); var id = await Start("PC-T");
        F.At(15, 0); Assert.Equal(HttpStatusCode.NoContent, (await Cp(id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CpAt(id, QaFactory.T(18, 0))).StatusCode);
        var inv = await Stop(id);
        Assert.True(inv.L("unroundedPayableToman") == 80_000, $"bill to last accepted checkpoint 15:00; got {inv.L("unroundedPayableToman")}");
    }
    [Fact] public async Task QA_T3_future_client_checkpoint_at_does_not_block_dead_pc_auto_close()
    {
        await F.ResetAsync(); await Reg("PC-T3", Normal); F.At(14, 0); var id = await Start("PC-T3");
        F.At(15, 0); Assert.Equal(HttpStatusCode.NoContent, (await Cp(id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CpAt(id, QaFactory.T(23, 0))).StatusCode);
        F.At(15, 10); await C.PostAsync("/api/internal/dead-pc-sweep", null);
        Assert.Equal("Closed", await SessionStatus(id));
    }

    // ---- Station ids, PO-7 row 86 ----
    [Fact] public async Task QA_N1_station_id_normalisation_rules()
    {
        await F.ResetAsync();
        await Reg(" PC-۰۱ ", Normal);
        foreach (var dup in new[] { "pc-01", "PC-01", "pc-٠١", "PC-０１", "  pC-01\t" })
            Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsJsonAsync("/api/stations", new { id = dup, name = "d", groupId = Normal })).StatusCode);
        foreach (var look in new[] { "pc-01", "PC-٠١", "pc-０1", " PC-01 " })
            Assert.Equal(HttpStatusCode.OK, (await C.GetAsync("/api/stations/" + Uri.EscapeDataString(look))).StatusCode);
        foreach (var other in new[] { "pc 01", "pc01", "PC_01", "PC--01" })
            Assert.Equal(HttpStatusCode.NotFound, (await C.GetAsync("/api/stations/" + Uri.EscapeDataString(other))).StatusCode);
        foreach (var other in new[] { "pc 01", "pc01" }) await Reg(other, Normal); // distinct ids
        await Reg("كي-1", Normal);
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsJsonAsync("/api/stations", new { id = "کی-1", name = "d", groupId = Normal })).StatusCode);
        Assert.Equal("کی-1", await F.Text("select \"Id\" from stations where \"Id\" like '%-1'"));
        Assert.Equal(4, await F.Scalar("select count(*) from stations"));
        // a session started by any spelling lands on the one station
        F.At(14, 0); Assert.Equal(HttpStatusCode.Created, (await C.PostAsJsonAsync("/api/sessions", new { stationId = "pc-۰1", playerCount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsJsonAsync("/api/sessions", new { stationId = "PC-01", playerCount = 1 })).StatusCode);
    }
}
