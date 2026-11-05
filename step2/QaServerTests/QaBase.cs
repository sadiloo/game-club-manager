using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
namespace Qa.Step2;

public abstract class QaBase(QaFactory f)
{
    protected readonly QaFactory F = f;
    protected readonly HttpClient C = f.Api();
    protected const string Normal = "pc-normal", Vip = "pc-vip";
    protected static string Ps5 => GameClub.Billing.GameTimerReference.Ps5GroupId;

    protected async Task Reg(string id, string group)
    {
        var r = await C.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = group });
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"register {id}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
    }
    protected async Task<Guid> Start(string station, int players = 1, string mode = "Offline")
    {
        var r = await C.PostAsJsonAsync("/api/sessions", new { stationId = station, playerCount = players, mode });
        Assert.True(r.StatusCode == HttpStatusCode.Created, $"start {station}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        return (await r.Json()).GetProperty("id").GetGuid();
    }
    /// Checkpoint at the server's current time (no client timestamp).
    protected async Task<HttpResponseMessage> Cp(Guid id) => await C.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { });
    protected async Task<HttpResponseMessage> CpAt(Guid id, DateTimeOffset at) => await C.PostAsJsonAsync($"/api/sessions/{id}/checkpoints", new { at });
    protected async Task<JsonElement> Stop(Guid id)
    {
        var r = await C.PostAsync($"/api/sessions/{id}/stop", null);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"stop: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        var json = await r.Json();
        if (json.TryGetProperty("pending", out var pending) && pending.GetBoolean())
        {
            await ConfirmPendingBill(id, json.L("collectibleToman"));
        }

        return json;
    }

    protected async Task ConfirmPendingBill(Guid sessionId, long collectibleToman)
    {
        var login = await C.PostAsJsonAsync("/api/operator/auth/login", new { username = "operator", pin = "1111" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var confirm = await C.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = collectibleToman } } });
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
    }
    protected async Task<long> Live(Guid id) => (await C.GetFromJsonAsync<JsonElement>($"/api/sessions/{id}/live-amount")).L("amountToman");

    /// PC session with a 1-minute checkpoint heartbeat from start to end (server time), then stop.
    protected async Task<JsonElement> PcRun(string station, (int h, int m) from, (int h, int m) to, int day = 3)
    {
        F.At(from.h, from.m, 0, day);
        var id = await Start(station);
        var t = FToMin(from); var end = FToMin(to); if (end <= t) end += 24 * 60;
        for (var x = t + 1; x <= end; x++) { var d = day + (x / 1440); F.At((x / 60) % 24, x % 60, 0, d); Assert.Equal(HttpStatusCode.NoContent, (await Cp(id)).StatusCode); }
        return await Stop(id);
    }
    static int FToMin((int h, int m) t) => t.h * 60 + t.m;

    protected static void Inv(JsonElement inv, long exact, long collect)
    {
        Assert.Equal(exact, inv.L("unroundedPayableToman"));
        Assert.Equal(collect, inv.L("collectibleToman"));
        Assert.Equal(collect - exact, inv.L("roundingToman"));
    }
    protected async Task<List<string>> AuditActions() => (await C.GetFromJsonAsync<List<JsonElement>>("/api/audit"))!.Select(e => e.GetProperty("action").GetString()!).ToList();
    protected Task<string?> SessionStatus(Guid id) => F.Text($"select \"Status\" from sessions where \"Id\"='{id}'");
}
