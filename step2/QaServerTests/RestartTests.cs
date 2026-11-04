using System.Net;
using System.Net.Http.Json;
namespace Qa.Step2;

/// Power cut / restart: a new host on the same DB resumes from the stored checkpoint.
public sealed class RestartTests
{
    static async Task<Guid> Begin(string db)
    {
        await using var a = new QaFactory(db, true, FT(14, 0));
        var c = a.Api();
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/stations", new { id = "PC-R", name = "r", groupId = "pc-normal" })).StatusCode);
        var id = (await (await c.PostAsJsonAsync("/api/sessions", new { stationId = "PC-R", playerCount = 1 })).Json()).GetProperty("id").GetGuid();
        for (var m = 1; m <= 60; m++) { a.Clock.SetUtc(FT(14 + m / 60, m % 60)); await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { }); }
        return id; // host disposed = power cut at 15:00
    }
    static DateTimeOffset FT(int h, int m) => QaFactory.T(h, m);

    [Fact] public async Task QA_R1_restart_within_timeout_resumes_and_bills_on()
    {
        var id = await Begin("gc_qa_restart1");
        await using var b = new QaFactory("gc_qa_restart1", false, FT(15, 5));
        var c = b.Api();
        Assert.Equal(0, (await (await c.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        for (var m = 6; m <= 6 + 0; m++) { b.Clock.SetUtc(FT(15, m)); Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { })).StatusCode); }
        var inv = await (await c.PostAsync($"/api/sessions/{id}/stop", null)).Json();
        Assert.Equal(88_000, inv.L("unroundedPayableToman")); // 66 min × 80,000/60
        Assert.Equal(90_000, inv.L("collectibleToman"));
    }

    [Fact] public async Task QA_R2_restart_after_timeout_auto_closes_to_last_checkpoint()
    {
        var id = await Begin("gc_qa_restart2");
        await using var b = new QaFactory("gc_qa_restart2", false, FT(15, 40));
        var c = b.Api();
        Assert.Equal(1, (await (await c.PostAsync("/api/internal/dead-pc-sweep", null)).Json()).L("closed"));
        Assert.Equal(80_000, await b.Scalar("select \"CollectibleToman\" from invoices"));
        Assert.Equal(1, await b.Scalar("select count(*) from audit_log where \"DetailsJson\" like '%auto-close%'"));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/sessions/{id}/stop", null)).StatusCode);
    }

    [Fact] public async Task QA_R3_hosted_sweep_closes_after_restart_without_manual_call()
    {
        var id = await Begin("gc_qa_restart3");
        await using var b = new HostedSweepFactory("gc_qa_restart3", FT(15, 10));
        var c = b.Api();
        await c.GetAsync("/api/stations"); // boot host
        for (var i = 0; i < 40 && await b.Scalar("select count(*) from invoices") == 0; i++) await Task.Delay(250);
        Assert.Equal(80_000, await b.Scalar("select \"CollectibleToman\" from invoices"));
    }

    sealed class HostedSweepFactory(string db, DateTimeOffset now) : QaFactory(db, false, now)
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder b)
        { base.ConfigureWebHost(b); b.UseSetting("DeadPc:SweepIntervalSeconds", "1"); }
    }
}
