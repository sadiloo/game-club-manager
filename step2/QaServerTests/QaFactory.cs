using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server;
using GameClub.Server.Data;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Qa.Step2;

/// QA's own host. Independent of develop's factory: own DB, hosted sweep effectively off
/// (interval 1 h) so only explicit sweeps close sessions; fresh=false re-uses the DB (restart tests).
public class QaFactory : WebApplicationFactory<Program>
{
    public static string BaseConn => Environment.GetEnvironmentVariable("QA_PG")
        ?? "Host=127.0.0.1;Port=55432;Username=qa;Password=qa";
    public MutableClock Clock { get; }
    public string Conn { get; }
    private readonly bool _fresh;

    public QaFactory(string db, bool fresh, DateTimeOffset now)
    { Conn = BaseConn + ";Database=" + db; _fresh = fresh; Clock = new MutableClock(now); }

    protected override void ConfigureWebHost(IWebHostBuilder b)
    {
        Environment.SetEnvironmentVariable("GAMECLUB_API_KEY", "qa-key");
        b.UseSetting("ConnectionStrings:Default", Conn);
        b.UseSetting("GAMECLUB_API_KEY", "qa-key");
        b.UseSetting("DeadPc:SweepIntervalSeconds", "3600");
        b.ConfigureServices(s => { s.RemoveAll<IClock>(); s.AddSingleton<IClock>(Clock); });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (_fresh) db.Database.EnsureDeleted();
        db.Database.Migrate();
        DatabaseSeed.EnsureAsync(db).GetAwaiter().GetResult();
        // same SQL as Program.ApplyAuditImmutabilityAsync (internal; runs in Main after Build, which the test host skips)
        db.Database.ExecuteSqlRaw("""
            CREATE OR REPLACE FUNCTION audit_log_immutable() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'audit_log is append-only'; END; $$ LANGUAGE plpgsql;
            DROP TRIGGER IF EXISTS audit_log_no_update ON audit_log;
            CREATE TRIGGER audit_log_no_update BEFORE UPDATE OR DELETE ON audit_log
              FOR EACH ROW EXECUTE FUNCTION audit_log_immutable();
            """);
        return host;
    }

    public HttpClient Api(string? key = "qa-key")
    {
        var c = CreateClient();
        if (key != null) c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }

    public void At(int h, int m, int s = 0, int day = 3) => Clock.SetTehran(2026, 6, day, h, m, s);
    public static DateTimeOffset T(int h, int m, int s = 0, int day = 3) => GameClub.Billing.TehranTime.At(2026, 6, day, h, m, s);

    public async Task ResetAsync()
    {
        await using var c = new NpgsqlConnection(Conn); await c.OpenAsync();
        await new NpgsqlCommand("""
            TRUNCATE audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, rate_card_snapshot, stations RESTART IDENTITY CASCADE;
            SELECT setval('invoice_number_seq', 1, false);
            UPDATE invoice_number_sequence SET "NextNumber" = 1;
            DELETE FROM rate_lines; DELETE FROM branch_rounding;
            """, c).ExecuteNonQueryAsync();
        using var scope = Services.CreateScope();
        await DatabaseSeed.EnsureAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<long> Scalar(string sql)
    {
        await using var c = new NpgsqlConnection(Conn); await c.OpenAsync();
        var o = await new NpgsqlCommand(sql, c).ExecuteScalarAsync();
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }
    public async Task<string?> Text(string sql)
    {
        await using var c = new NpgsqlConnection(Conn); await c.OpenAsync();
        return (await new NpgsqlCommand(sql, c).ExecuteScalarAsync())?.ToString();
    }
    public async Task Exec(string sql)
    {
        await using var c = new NpgsqlConnection(Conn); await c.OpenAsync();
        await new NpgsqlCommand(sql, c).ExecuteNonQueryAsync();
    }
}

public sealed class MainFactory() : QaFactory("gc_qa_main", true, DateTimeOffset.UtcNow);
[CollectionDefinition("qa")] public sealed class QaCollection : ICollectionFixture<MainFactory>;

public static class Http
{
    public static Task<HttpResponseMessage> Patch<T>(this HttpClient c, string url, T body)
        => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) });
    public static async Task<JsonElement> Json(this HttpResponseMessage r)
    {
        var s = await r.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(s) ? default : JsonDocument.Parse(s).RootElement.Clone();
    }
    public static long L(this JsonElement e, string p) => e.GetProperty(p).GetInt64();
}
