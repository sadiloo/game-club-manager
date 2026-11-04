using System.Net;
using System.Net.Http.Json;
namespace Qa.Step2;

/// QA-S01..S15 + CN-3, spec /workspace/specs/step2-server-scope.md "Acceptance scenarios".
[Collection("qa")]
public sealed class ScenarioTests(MainFactory f) : QaBase(f)
{
    [Fact] public async Task QA_S01_register_normalises_and_duplicate_409_nothing_stored()
    {
        await F.ResetAsync();
        var r = await C.PostAsJsonAsync("/api/stations", new { id = " PC-۰۱ ", name = "PC 1", groupId = Normal });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("PC-01", (await r.Json()).GetProperty("id").GetString()); // engine form (PO clarification S1)
        var auditBefore = await F.Scalar("select count(*) from audit_log");
        var dup = await C.PostAsJsonAsync("/api/stations", new { id = "pc-01", name = "dup", groupId = Normal });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal(1, await F.Scalar("select count(*) from stations"));
        Assert.Equal("PC 1", await F.Text("select \"Name\" from stations"));
        Assert.Equal(auditBefore, await F.Scalar("select count(*) from audit_log"));
    }

    [Fact] public async Task QA_S02_normal_pc_14_15_is_80000()
    { await F.ResetAsync(); await Reg("PC-A", Normal); Inv(await PcRun("PC-A", (14, 0), (15, 0)), 80_000, 80_000); }

    [Fact] public async Task QA_S03_switch_to_vip_group_from_directory_not_request_110000()
    {
        await F.ResetAsync(); await Reg("PC-B", Normal);
        Assert.Equal(HttpStatusCode.OK, (await C.Patch("/api/stations/pc-b/group", new { groupId = Vip })).StatusCode);
        F.At(14, 0);
        // request tries to smuggle a group; must be ignored
        var r = await C.PostAsJsonAsync("/api/sessions", new { stationId = "PC-B", playerCount = 1, groupId = Normal });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        var id = (await r.Json()).GetProperty("id").GetGuid();
        for (var m = 1; m <= 60; m++) { F.At(14 + m / 60, m % 60); await Cp(id); }
        Inv(await Stop(id), 110_000, 110_000);
        Assert.Equal(Vip, await F.Text($"select \"GroupId\" from play_segments where \"SessionId\"='{id}'"));
    }

    [Fact] public async Task QA_S04_normal_50min_exact_66667_collect_70000_both_stored()
    {
        await F.ResetAsync(); await Reg("PC-C", Normal);
        var inv = await PcRun("PC-C", (14, 0), (14, 50));
        Inv(inv, 66_667, 70_000);
        Assert.Equal(66_667, await F.Scalar("select \"UnroundedPayableToman\" from invoices"));
        Assert.Equal(70_000, await F.Scalar("select \"CollectibleToman\" from invoices"));
        Assert.Equal(3_333, await F.Scalar("select \"RoundingToman\" from invoices"));
    }

    [Fact] public async Task QA_S05_normal_08_09_crosses_band_90000()
    { await F.ResetAsync(); await Reg("PC-D", Normal); Inv(await PcRun("PC-D", (8, 0), (9, 0)), 90_000, 90_000); }

    [Fact] public async Task QA_S06_vip_02_03_special_130000()
    { await F.ResetAsync(); await Reg("PC-E", Vip); Inv(await PcRun("PC-E", (2, 0), (3, 0)), 130_000, 130_000); }

    [Fact] public async Task QA_S07_real_bill_ps5_2p_14_00_17_36_450000()
    {
        await F.ResetAsync(); await Reg("PS5-1", Ps5); F.At(14, 0);
        var id = await Start("PS5-1", 2); F.At(17, 36); Inv(await Stop(id), 450_000, 450_000);
    }

    [Fact] public async Task QA_S08_real_bill_ps5_3p_14_00_15_45_280000()
    {
        await F.ResetAsync(); await Reg("PS5-2", Ps5); F.At(14, 0);
        var id = await Start("PS5-2", 3); F.At(15, 45); Inv(await Stop(id), 280_000, 280_000);
    }

    [Fact] public async Task QA_S09_ps5_pause_resume_60min_110000_audited()
    {
        await F.ResetAsync(); await Reg("PS5-3", Ps5); F.At(14, 0);
        var id = await Start("PS5-3", 1);
        F.At(14, 30); Assert.Equal(HttpStatusCode.NoContent, (await C.PostAsync($"/api/sessions/{id}/pause", null)).StatusCode);
        F.At(14, 40);
        Assert.Equal(55_000, await Live(id)); // paused time not billed
        F.At(14, 50); Assert.Equal(HttpStatusCode.NoContent, (await C.PostAsync($"/api/sessions/{id}/resume", null)).StatusCode);
        F.At(15, 20); Inv(await Stop(id), 110_000, 110_000);
        var a = await AuditActions();
        foreach (var x in new[] { "session.start", "session.pause", "session.resume", "session.stop" }) Assert.Contains(x, a);
    }

    [Fact] public async Task QA_S10_pause_on_pc_409_time_keeps_running()
    {
        await F.ResetAsync(); await Reg("PC-F", Normal); F.At(14, 0);
        var id = await Start("PC-F");
        F.At(14, 30); await Cp(id);
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsync($"/api/sessions/{id}/pause", null)).StatusCode);
        Assert.Equal("Open", await SessionStatus(id));
        Assert.Equal(0, await F.Scalar("select count(*) from pause_intervals"));
        F.At(15, 0); await Cp(id);
        Assert.Equal(80_000, await Live(id));
        Inv(await Stop(id), 80_000, 80_000);
    }

    [Fact] public async Task QA_S11_live_amount_55000_unrounded_session_stays_open()
    {
        await F.ResetAsync(); await Reg("PS5-4", Ps5); F.At(14, 0);
        var id = await Start("PS5-4", 1);
        F.At(14, 30); Assert.Equal(55_000, await Live(id));
        Assert.Equal("Open", await SessionStatus(id));
        Assert.Equal(0, await F.Scalar("select count(*) from invoices"));
        F.At(14, 10 + 20, 30); Assert.Equal(56_833, await Live(id)); // 31 started min × 110,000/60 = 56,833.3 → 56,833 (unrounded)
    }

    [Fact] public async Task QA_S12_started_minute_65s_3667_collect_5000()
    {
        await F.ResetAsync(); await Reg("PS5-5", Ps5); F.At(14, 0, 0);
        var id = await Start("PS5-5", 1); F.At(14, 1, 5); Inv(await Stop(id), 3_667, 5_000);
    }

    [Fact] public async Task QA_S13_dead_pc_auto_close_at_ge_10min_to_last_checkpoint_80000()
    {
        await F.ResetAsync(); await Reg("PC-G", Normal);
        F.At(14, 0); var id = await Start("PC-G");
        for (var m = 1; m <= 60; m++) { F.At(14 + m / 60, m % 60); await Cp(id); }
        F.At(15, 9, 59);
        Assert.Equal(0, (await (await C.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        F.At(15, 10, 0); // exactly 10 counts
        Assert.Equal(1, (await (await C.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        Assert.Equal(0, (await (await C.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed")); // idempotent
        Assert.Equal("Closed", await SessionStatus(id));
        Assert.Equal(80_000, await F.Scalar("select \"CollectibleToman\" from invoices"));
        Assert.Equal(80_000, await F.Scalar("select \"UnroundedPayableToman\" from invoices"));
        Assert.Equal(1, await F.Scalar("select count(*) from audit_log where \"Action\"='session.stop' and \"DetailsJson\" like '%auto-close%'"));
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
    }

    [Fact] public async Task QA_S14_rejected_checkpoint_manual_settle_queue_no_invoice()
    {
        await F.ResetAsync(); await Reg("PC-H", Normal);
        F.At(14, 0); var id = await Start("PC-H");
        F.At(15, 0); await Cp(id);
        F.At(15, 1); await CpAt(id, QaFactory.T(14, 30)); // report earlier than previous
        Assert.Equal("ManualSettle", await SessionStatus(id));
        Assert.Equal(HttpStatusCode.Conflict, (await C.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await C.GetAsync($"/api/sessions/{id}/live-amount")).StatusCode);
        Assert.Equal(0, await F.Scalar("select count(*) from invoices"));
        Assert.Equal(1, await F.Scalar("select count(*) from audit_log where \"Action\"='session.manual_settle'"));
        // the dead-PC sweep must not auto-close a queued session either
        F.At(15, 30); await C.PostAsync("/api/internal/dead-pc-sweep", null);
        Assert.Equal(0, await F.Scalar("select count(*) from invoices"));
    }

    [Fact] public async Task QA_S15_audit_rows_for_all_actions_and_db_rejects_update_delete()
    {
        await F.ResetAsync();
        await Reg("PC-I", Normal);
        await C.Patch("/api/stations/PC-I", new { name = "renamed" });
        await C.Patch("/api/stations/PC-I/group", new { groupId = Vip });
        var rates = await C.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/rates");
        Assert.Equal(HttpStatusCode.OK, (await C.PutAsJsonAsync("/api/rates", rates)).StatusCode);
        await Reg("PS5-A", Ps5);
        F.At(14, 0); var p = await Start("PS5-A"); F.At(14, 5); await C.PostAsync($"/api/sessions/{p}/pause", null);
        F.At(14, 6); await C.PostAsync($"/api/sessions/{p}/resume", null); F.At(14, 7); await Stop(p);
        await Reg("PC-J", Normal); await C.PostAsync("/api/stations/PC-J/deactivate", null);
        var a = await AuditActions();
        foreach (var x in new[] { "station.register", "station.update", "rate.change", "session.start", "session.pause", "session.resume", "session.stop", "station.deactivate" })
            Assert.Contains(x, a);
        // correct (quoted) column names, so the trigger, not a typo, is what rejects
        var ex1 = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => F.Exec("UPDATE audit_log SET \"Action\"='tamper' WHERE \"Id\"=1"));
        Assert.Contains("append-only", ex1.MessageText);
        var ex2 = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => F.Exec("DELETE FROM audit_log WHERE \"Id\"=1"));
        Assert.Contains("append-only", ex2.MessageText);
        // app: no HTTP route mutates the log
        foreach (var m in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
            Assert.True((int)(await C.SendAsync(new HttpRequestMessage(m, "/api/audit/1"))).StatusCode is 404 or 405);
        Assert.True((int)(await C.DeleteAsync("/api/audit")).StatusCode is 404 or 405);
    }

    [Fact] public async Task QA_CN3_controller_change_2p_then_1p_235000_audited()
    {
        await F.ResetAsync(); await Reg("PS5-CN", Ps5); F.At(14, 0);
        var id = await Start("PS5-CN", 2); F.At(15, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await C.Patch($"/api/sessions/{id}/player-count", new { playerCount = 1 })).StatusCode);
        F.At(16, 0); Inv(await Stop(id), 235_000, 235_000);
        Assert.Contains("session.player_count", await AuditActions());
    }
}
