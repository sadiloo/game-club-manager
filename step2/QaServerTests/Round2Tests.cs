using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
namespace Qa.Step2;

/// Fix round 1 (6ec1793): PO default 9 edges, defaults 8/10/11/12, regressions, high-parallelism hammers.
[Collection("qa")]
public sealed class Round2Tests(MainFactory f) : QaBase(f)
{
    async Task<DateTime?> LastCp(Guid id) { var e = await F.Scalar($"select extract(epoch from \"LastCheckpointUtc\")::bigint from sessions where \"Id\"='{id}'"); return DateTimeOffset.FromUnixTimeSeconds(e).UtcDateTime; }
    static DateTimeOffset Plus(int h, int m, int s) => QaFactory.T(h, m).AddSeconds(s);

    // ---- PO default 9 ----
    [Fact] public async Task QA_P9a_past_at_recorded_with_po_max_min_formula()
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(15, 0); Assert.Equal(HttpStatusCode.NoContent, (await CpAt(id, QaFactory.T(14, 30))).StatusCode);
        Assert.Equal("Open", await SessionStatus(id));
        Assert.Equal(QaFactory.T(14, 30).UtcDateTime, (await LastCp(id))!.Value);
        Inv(await Stop(id), 40_000, 40_000);
    }
    [Fact] public async Task QA_P9b_at_now_plus_59s_recorded_at_receipt()
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(15, 0); Assert.Equal(HttpStatusCode.NoContent, (await CpAt(id, Plus(15, 0, 59))).StatusCode);
        Inv(await Stop(id), 80_000, 80_000); // 15:00:59 would be 61 started min = 81,333
    }
    [Fact] public async Task QA_P9c_at_now_plus_60s_boundary_accepted_at_receipt()
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(15, 0); Assert.Equal(HttpStatusCode.NoContent, (await CpAt(id, Plus(15, 0, 60))).StatusCode);
        Inv(await Stop(id), 80_000, 80_000);
    }
    [Fact] public async Task QA_P9d_at_now_plus_61s_400_last_checkpoint_unchanged_not_audited()
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(14, 50); await Cp(id);
        F.At(15, 0);
        var r = await CpAt(id, Plus(15, 0, 61));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("checkpoint_in_future", await r.Content.ReadAsStringAsync());
        Assert.Equal(QaFactory.T(14, 50).UtcDateTime, (await LastCp(id))!.Value);
        Assert.Equal(1, await F.Scalar("select count(*) from checkpoints"));
        Assert.Equal("Open", await SessionStatus(id));
        Inv(await Stop(id), 66_667, 70_000); // stop after rejected checkpoint works, bills 14:50
    }
    [Theory]
    [InlineData("\"yesterday\"")] [InlineData("12345")] [InlineData("\"2026-13-45T25:00:00+03:30\"")] [InlineData("true")] [InlineData("{}")]
    public async Task QA_P9e_malformed_at_is_400_and_changes_nothing(string raw)
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(14, 30);
        var r = await C.PostAsync($"/api/sessions/{id}/checkpoints", new StringContent("{\"at\":" + raw + "}", Encoding.UTF8, "application/json"));
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"{raw}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        Assert.Equal(QaFactory.T(14, 0).UtcDateTime, (await LastCp(id))!.Value);
        Assert.Equal("Open", await SessionStatus(id));
    }
    [Fact] public async Task QA_P9f_rejected_future_checkpoint_does_not_extend_auto_close()
    {
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(15, 0); await Cp(id);
        F.At(15, 5); Assert.Equal(HttpStatusCode.BadRequest, (await CpAt(id, QaFactory.T(15, 30))).StatusCode);
        F.At(15, 9, 59); Assert.Equal(0, (await (await C.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        F.At(15, 10); Assert.Equal(1, (await (await C.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        Assert.Equal(80_000, await F.Scalar("select \"CollectibleToman\" from invoices"));
    }
    [Fact] public async Task QA_P9g_offset_variants_same_instant()
    {
        // +04:30 and Z forms of an instant inside the window are fine; recorded at receipt
        await F.ResetAsync(); await Reg("PC-P", Normal); F.At(14, 0); var id = await Start("PC-P");
        F.At(15, 0);
        var r = await C.PostAsync($"/api/sessions/{id}/checkpoints", new StringContent("{\"at\":\"" + QaFactory.T(15, 0).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var r2 = await C.PostAsync($"/api/sessions/{id}/checkpoints", new StringContent("{\"at\":\"" + QaFactory.T(15, 0).ToOffset(TimeSpan.FromHours(4.5)).AddMinutes(5).ToString("yyyy-MM-ddTHH:mm:sszzz") + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode); // 5 min ahead
    }

    // ---- defaults 8, 11, 12 and manual-settle interplay ----
    [Fact] public async Task QA_D8_group_change_blocked_while_session_not_closed()
    {
        await F.ResetAsync(); await Reg("PC-G8", Normal); F.At(14, 0); var id = await Start("PC-G8");
        Assert.Equal(HttpStatusCode.Conflict, (await C.Patch("/api/stations/PC-G8/group", new { groupId = Vip })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await C.Patch("/api/stations/PC-G8", new { groupId = Vip })).StatusCode);
        F.At(15, 0); await Cp(id); F.At(15, 1); await CpAt(id, QaFactory.T(14, 30)); // -> ManualSettle
        Assert.Equal(HttpStatusCode.Conflict, (await C.Patch("/api/stations/PC-G8/group", new { groupId = Vip })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await C.Patch("/api/stations/PC-G8/group", new { groupId = Vip })).StatusCode);
        Assert.Equal(Vip, await F.Text("select \"GroupId\" from stations"));
    }
    [Fact] public async Task QA_D11_rates_locked_at_start()
    {
        await F.ResetAsync(); await Reg("PS5-L", Ps5); await Reg("PS5-L2", Ps5); F.At(14, 0);
        var a = await Start("PS5-L");
        var rates = await C.GetFromJsonAsync<JsonElement>("/api/rates");
        var body = new { roundingStepToman = 5000, roundingDirection = "Up", lines = new[] { new { groupId = Ps5, playMode = "Offline", playerCount = (int?)1, isSpecialBand = false, hourlyToman = 150_000L } } };
        Assert.Equal(HttpStatusCode.OK, (await C.PutAsJsonAsync("/api/rates", body)).StatusCode);
        var b = await Start("PS5-L2");
        F.At(14, 30); Assert.Equal(55_000, await Live(a)); Assert.Equal(75_000, await Live(b));
        F.At(15, 0);
        Inv(await Stop(a), 110_000, 110_000);
        Inv(await Stop(b), 150_000, 150_000);
    }
    [Fact] public async Task QA_D12_manual_settle_session_does_not_block_new_start_and_both_bill()
    {
        await F.ResetAsync(); await Reg("PC-12", Normal); F.At(14, 0); var a = await Start("PC-12");
        F.At(15, 0); await Cp(a); F.At(15, 1); await CpAt(a, QaFactory.T(14, 30));
        Assert.Equal("ManualSettle", await SessionStatus(a));
        F.At(15, 2); var b = await Start("PC-12");
        F.At(16, 2); await Cp(b);
        Inv(await Stop(b), 80_000, 80_000);
        var r = await C.PostAsJsonAsync($"/api/sessions/{a}/manual-settle", new { });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); Inv(await r.Json(), 80_000, 80_000);
        Assert.Equal(2, await F.Scalar("select count(*) from invoices"));
        Assert.Equal(2, await F.Scalar("select max(\"Number\") from invoices"));
    }
    [Fact] public async Task QA_R2_stop_after_backwards_checkpoint_409_then_manual_settle_endAt()
    {
        await F.ResetAsync(); await Reg("PC-B", Normal); F.At(14, 0); var id = await Start("PC-B");
        F.At(15, 0); await Cp(id); F.At(15, 1); await CpAt(id, QaFactory.T(14, 30));
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Cp(id)).StatusCode); // no more checkpoints while queued
        F.At(15, 30);
        var r = await C.PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { endAt = QaFactory.T(15, 15) });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); Inv(await r.Json(), 100_000, 100_000);
    }

    // ---- rates validation must still accept the pilot card ----
    [Fact] public async Task QA_RV1_pilot_rates_round_trip_and_case_insensitive_direction()
    {
        await F.ResetAsync();
        var before = await C.GetStringAsync("/api/rates");
        var rates = JsonDocument.Parse(before).RootElement;
        Assert.Equal(HttpStatusCode.OK, (await C.PutAsJsonAsync("/api/rates", rates)).StatusCode);
        Assert.Equal(before, await C.GetStringAsync("/api/rates"));
        Assert.Equal(HttpStatusCode.OK, (await C.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000, roundingDirection = "up", lines = Array.Empty<object>() })).StatusCode);
        Assert.Equal(12, rates.GetProperty("lines").GetArrayLength());
        // all pilot amounts still produce the scenario totals after a round trip
        await Reg("PC-RV", Normal); Inv(await PcRun("PC-RV", (8, 0), (9, 0)), 90_000, 90_000);
    }
    [Fact] public async Task QA_RV2_negative_pc_special_surcharge_rejected()
    {
        await F.ResetAsync();
        var r = await C.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000, roundingDirection = "Up", lines = Array.Empty<object>(), pcSpecialBandSurchargeToman = -100_000L });
        var stored = (await C.GetFromJsonAsync<JsonElement>("/api/rates")).L("pcSpecialBandSurchargeToman");
        await F.ResetAsync();
        Assert.True(r.StatusCode == HttpStatusCode.BadRequest, $"negative surcharge: {(int)r.StatusCode}, stored {stored}");
    }

    // ---- races ----
    [Fact] public async Task QA_H1_concurrent_start_32x_10_rounds()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PS5-H{round}"; await Reg(st, Ps5); F.At(14, 0);
            var codes = (await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => F.Api().PostAsJsonAsync("/api/sessions", new { stationId = st, playerCount = 1 })))).Select(r => (int)r.StatusCode).ToList();
            var open = await F.Scalar($"select count(*) from sessions where \"StationId\"='{st}' and \"Status\" in ('Open','Paused')");
            if (open != 1 || codes.Count(c => c == 201) != 1 || codes.Any(c => c is not (201 or 409))) bad.Add($"r{round}: open={open} codes={string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
    [Fact] public async Task QA_H2_concurrent_pause_16x_10_rounds()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PS5-P{round}"; await Reg(st, Ps5); F.At(14, 0); var id = await Start(st); F.At(14, 10);
            var codes = (await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/pause", null)))).Select(r => (int)r.StatusCode).ToList();
            var n = await F.Scalar($"select count(*) from pause_intervals where \"SessionId\"='{id}'");
            F.At(14, 20); await C.PostAsync($"/api/sessions/{id}/resume", null); F.At(15, 0);
            var inv = await Stop(id);
            if (n != 1 || codes.Any(c => c is not (204 or 409)) || inv.L("unroundedPayableToman") != 91_667) bad.Add($"r{round}: pauses={n} billed={inv.L("unroundedPayableToman")} codes={string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
    [Fact] public async Task QA_H3_pause_resume_race_keeps_status_and_pause_rows_consistent()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PS5-PR{round}"; await Reg(st, Ps5); F.At(14, 0); var id = await Start(st); F.At(14, 10);
            await C.PostAsync($"/api/sessions/{id}/pause", null);
            F.At(14, 20);
            var tasks = Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? F.Api().PostAsync($"/api/sessions/{id}/resume", null) : F.Api().PostAsync($"/api/sessions/{id}/pause", null));
            var codes = (await Task.WhenAll(tasks)).Select(r => (int)r.StatusCode).ToList();
            var status = await SessionStatus(id);
            var openP = await F.Scalar($"select count(*) from pause_intervals where \"SessionId\"='{id}' and \"EndUtc\" is null");
            if (codes.Any(c => c >= 500) || (status == "Paused") != (openP == 1) || openP > 1) bad.Add($"r{round}: status={status} openPauses={openP} codes={string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
    [Fact] public async Task QA_H4_concurrent_resume_single_resume_audited()
    {
        await F.ResetAsync(); await Reg("PS5-RR", Ps5); F.At(14, 0); var id = await Start("PS5-RR");
        F.At(14, 10); await C.PostAsync($"/api/sessions/{id}/pause", null); F.At(14, 20);
        var codes = (await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/resume", null)))).Select(r => (int)r.StatusCode).ToList();
        var audits = await F.Scalar("select count(*) from audit_log where \"Action\"='session.resume'");
        Assert.True(codes.Count(c => c == 204) == 1 && audits == 1 && codes.All(c => c is 204 or 409), $"resume 204s={codes.Count(c => c == 204)} audit rows={audits} codes={string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
    }
    [Fact] public async Task QA_H5_parallel_stop_30_sessions_unique_contiguous_numbers_10_rounds()
    {
        await F.ResetAsync(); var bad = new List<string>(); var total = 0;
        for (var round = 0; round < 10; round++)
        {
            F.At(14, 0); var ids = new List<Guid>();
            for (var i = 0; i < 30; i++) { var st = $"PS5-S{round}-{i}"; await Reg(st, Ps5); ids.Add(await Start(st)); }
            F.At(15, 0);
            var codes = (await Task.WhenAll(ids.Select(id => F.Api().PostAsync($"/api/sessions/{id}/stop", null)))).Select(r => (int)r.StatusCode).ToList();
            total += 30;
            if (codes.Any(c => c != 200)) bad.Add($"r{round}: {string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        var n = await F.Scalar("select count(*) from invoices"); var d = await F.Scalar("select count(distinct \"Number\") from invoices"); var mx = await F.Scalar("select max(\"Number\") from invoices");
        Assert.True(bad.Count == 0 && n == total && d == total && mx == total, $"invoices={n} distinct={d} max={mx} expected {total}; {string.Join(" | ", bad)}");
    }
    [Fact] public async Task QA_H6_concurrent_stop_same_session_16x_10_rounds_numbers_contiguous()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PS5-X{round}"; await Reg(st, Ps5); F.At(14, 0); var id = await Start(st); F.At(15, 0);
            var codes = (await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => F.Api().PostAsync($"/api/sessions/{id}/stop", null)))).Select(r => (int)r.StatusCode).ToList();
            if (codes.Count(c => c == 200) != 1 || codes.Any(c => c is not (200 or 409))) bad.Add($"r{round}: {string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        var mx = await F.Scalar("select max(\"Number\") from invoices"); var n = await F.Scalar("select count(*) from invoices");
        Assert.True(bad.Count == 0 && n == 10 && mx == 10, $"invoices={n} maxNumber={mx}; {string.Join(" | ", bad)}");
    }
    [Fact] public async Task QA_H7_concurrent_manual_settle_one_invoice_no_number_gap()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PC-MS{round}"; await Reg(st, Normal); F.At(14, 0); var id = await Start(st);
            F.At(15, 0); await Cp(id); F.At(15, 1); await CpAt(id, QaFactory.T(14, 30)); F.At(15, 2);
            var codes = (await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => F.Api().PostAsJsonAsync($"/api/sessions/{id}/manual-settle", new { })))).Select(r => (int)r.StatusCode).ToList();
            if (codes.Count(c => c == 200) != 1 || codes.Any(c => c is not (200 or 409))) bad.Add($"r{round}: {string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        var n = await F.Scalar("select count(*) from invoices"); var mx = await F.Scalar("select max(\"Number\") from invoices");
        var resolves = await F.Scalar("select count(*) from audit_log where \"Action\"='session.manual_settle_resolve'");
        Assert.True(bad.Count == 0 && n == 10 && mx == 10 && resolves == 10, $"invoices={n} maxNumber={mx} resolveAudits={resolves}; {string.Join(" | ", bad)}");
    }
    [Fact] public async Task QA_H8_stop_vs_dead_pc_sweep_race_one_invoice_no_gap()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PC-SW{round}"; await Reg(st, Normal); F.At(14, 0); var id = await Start(st);
            F.At(15, 0); await Cp(id); F.At(15, 10);
            var t = new List<Task<HttpResponseMessage>>();
            for (var i = 0; i < 4; i++) { t.Add(F.Api().PostAsync($"/api/sessions/{id}/stop", null)); t.Add(F.Api().PostAsync("/api/internal/dead-pc-sweep", null)); }
            var codes = (await Task.WhenAll(t)).Select(r => (int)r.StatusCode).ToList();
            if (codes.Any(c => c >= 500)) bad.Add($"r{round}: {string.Join(",", codes)}");
        }
        var n = await F.Scalar("select count(*) from invoices"); var mx = await F.Scalar("select max(\"Number\") from invoices");
        var amounts = await F.Scalar("select count(*) from invoices where \"CollectibleToman\"<>80000");
        Assert.True(bad.Count == 0 && n == 10 && mx == 10 && amounts == 0, $"invoices={n} maxNumber={mx} wrongAmounts={amounts}; {string.Join(" | ", bad)}");
    }
    [Fact] public async Task QA_H9_concurrent_register_16_spellings_10_rounds()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var d = (char)('0' + round);
            var spell = new[] { $" pc-۷{d} ", $"PC-7{d}", $"pc-٧{d}", $"PC-７{d}", $"pc-7{d}", $"Pc-7{d}", $" PC-7{d}", $"pc-7{d} " };
            var codes = (await Task.WhenAll(spell.Concat(spell).Select(i => F.Api().PostAsJsonAsync("/api/stations", new { id = i, name = i, groupId = Normal })))).Select(r => (int)r.StatusCode).ToList();
            if (codes.Count(c => c == 201) != 1 || codes.Any(c => c is not (201 or 409))) bad.Add($"r{round}: {string.Join(",", codes.GroupBy(c => c).Select(g => $"{g.Key}x{g.Count()}"))}");
        }
        Assert.Equal(10, await F.Scalar("select count(*) from stations"));
        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }

    [Fact] public async Task QA_H3b_pause_resume_race_money_impact()
    {
        await F.ResetAsync(); var bad = new List<string>();
        for (var round = 0; round < 10; round++)
        {
            var st = $"PS5-PM{round}"; await Reg(st, Ps5); F.At(14, 0); var id = await Start(st);
            F.At(14, 10); await C.PostAsync($"/api/sessions/{id}/pause", null);
            F.At(14, 20);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? F.Api().PostAsync($"/api/sessions/{id}/pause", null) : F.Api().PostAsync($"/api/sessions/{id}/resume", null)));
            // operator makes sure it is running: resume if paused
            if (await SessionStatus(id) == "Paused") await C.PostAsync($"/api/sessions/{id}/resume", null);
            F.At(15, 0);
            var status = await SessionStatus(id);
            var inv = await Stop(id);
            // one real pause 14:10-14:20; every raced pause/resume is at 14:20 (zero length): 50 min = 91,667
            if (inv.L("unroundedPayableToman") != 91_667) bad.Add($"r{round}: status-before-stop={status} billed={inv.L("unroundedPayableToman")}");
        }
        Assert.True(bad.Count == 0, "expected 91,667 each: " + string.Join(" | ", bad));
    }
    [Fact] public async Task QA_RV2b_negative_surcharge_money_impact()
    {
        await F.ResetAsync();
        await C.PutAsJsonAsync("/api/rates", new { roundingStepToman = 5000, roundingDirection = "Up", lines = Array.Empty<object>(), pcSpecialBandSurchargeToman = -100_000L });
        await Reg("PC-NS", Normal);
        F.At(2, 0); var id = await Start("PC-NS"); F.At(3, 0); await Cp(id);
        var r = await C.PostAsync($"/api/sessions/{id}/stop", null);
        var body = await r.Content.ReadAsStringAsync();
        await F.ResetAsync();
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"stop after surcharge -100,000 accepted by PUT /api/rates: {(int)r.StatusCode} {body[..Math.Min(160, body.Length)]}");
    }
    [Fact] public async Task QA_H8b_stop_vs_sweep_error_body()
    {
        await F.ResetAsync(); var bodies = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            var st = $"PC-SB{round}"; await Reg(st, Normal); F.At(14, 0); var id = await Start(st);
            F.At(15, 0); await Cp(id); F.At(15, 10);
            var rs = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? F.Api().PostAsync($"/api/sessions/{id}/stop", null) : F.Api().PostAsync("/api/internal/dead-pc-sweep", null)));
            foreach (var r in rs) bodies.Add($"{(int)r.StatusCode}:{(await r.Content.ReadAsStringAsync())[..Math.Min(200, (await r.Content.ReadAsStringAsync()).Length)]}");
        }
        Assert.True(bodies.All(b => !b.StartsWith("5")), string.Join(" || ", bodies));
    }
}
