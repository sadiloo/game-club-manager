using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace GameClub.Server.Tests;

public class GameClubWebApplicationFactory : WebApplicationFactory<Program>
{
    public MutableClock Clock { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>
    /// When false (default), the test host does not run periodic dead-PC sweeps.
    /// Enable only in tests that assert on the hosted background timer.
    /// </summary>
    public bool DeadPcBackgroundSweepEnabled { get; set; }

    public string ConnectionString { get; private set; } = "";

    public HttpClient CreateClientWithKey()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, "dev-key");
        return client;
    }

    public async Task<HttpClient> CreateOperatorClientAsync(string username = "operator", string pin = "1111")
    {
        var client = CreateClient();
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username, pin });
        login.EnsureSuccessStatusCode();
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConnectionString = Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
            ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
            ?? "Host=localhost;Database=gameclub_test;Username=gameclub;Password=gameclub";

        Environment.SetEnvironmentVariable("GAMECLUB_DATABASE", ConnectionString);
        Environment.SetEnvironmentVariable("GAMECLUB_API_KEY", "dev-key");
        Environment.SetEnvironmentVariable("DeadPc__SweepIntervalSeconds", "1");

        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting($"{DeadPcSweepOptions.SectionName}:SweepIntervalSeconds", "1");
        builder.UseSetting("OperatorSessionSecret", "test-operator-secret");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton(_ => this);
            services.RemoveAll<IHostedService>();
            services.AddHostedService<TestDeadPcSweepBackgroundService>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(ConnectionString).Options;
        using (var bootstrap = new AppDbContext(options))
        {
            bootstrap.Database.EnsureDeleted();
        }

        var host = base.CreateHost(builder);
        SeedDatabase(host);
        return host;
    }

    private static void SeedDatabase(IHost host)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        DatabaseSeed.EnsureAsync(db).GetAwaiter().GetResult();
        Program.ApplyAuditImmutabilityAsync(db).GetAwaiter().GetResult();
    }
}

internal static class S3TestHelpers
{
    public const string TruncateSql = """
        TRUNCATE invoice_catalog_lines, open_sale_lines, open_sales, catalog_items, catalog_categories RESTART IDENTITY CASCADE;
        TRUNCATE rejected_card_to_card, payment_lines, pending_bills, audit_log, checkpoints, pause_intervals, play_segments, invoices, sessions, rate_card_snapshot, stations, destination_cards RESTART IDENTITY CASCADE;
        TRUNCATE operator_users RESTART IDENTITY CASCADE;
        SELECT setval('invoice_number_seq', 1, false);
        UPDATE invoice_number_sequence SET "NextNumber" = 1;
        UPDATE rate_lines SET "HourlyToman" = 80000
          WHERE "GroupId" = 'pc-normal' AND "IsSpecialBand" = false AND "PlayMode" = 'Offline' AND "PlayerCount" = 1;
        """;

    public static async Task ResetAsync(GameClubWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(TruncateSql);
        await DatabaseSeed.EnsureAsync(db);
    }

    public static async Task FinishPcPaymentAsync(GameClubWebApplicationFactory factory, Guid sessionId, long amountToman)
    {
        var op = await factory.CreateOperatorClientAsync();
        await ConfirmCashAsync(op, sessionId, amountToman);
    }

    public static async Task ConfirmCashAsync(HttpClient op, Guid sessionId, long amountToman)
    {
        var response = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = amountToman } } });
        response.EnsureSuccessStatusCode();
    }

    public static void AssertMoney(JsonElement body, long unrounded, long collectible)
    {
        Assert.Equal(unrounded, body.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(collectible, body.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(collectible - unrounded, body.GetProperty("roundingToman").GetInt64());
    }

    public static DateTimeOffset T(int y, int m, int d, int h, int min, int s = 0)
        => GameClub.Billing.TehranTime.At(y, m, d, h, min, s);

    public static async Task RegisterConsole(HttpClient client, HttpClient op, string id, int defaultPlayers = 1)
    {
        var response = await client.PostAsJsonAsync("/api/stations", new { id, name = id, groupId = GameClub.Billing.GameTimerReference.Ps5GroupId });
        response.EnsureSuccessStatusCode();
        await op.PatchAsJsonAsync($"/api/operator/stations/{id}/defaults", new { playerCount = defaultPlayers, mode = "Offline" });
    }

    public static async Task RegisterPc(HttpClient client, string id, string groupId)
    {
        var response = await client.PostAsJsonAsync("/api/stations", new { id, name = id, groupId });
        response.EnsureSuccessStatusCode();
    }

    public static async Task<Guid> StartSession(HttpClient client, string stationId, int playerCount = 1)
    {
        var response = await client.PostAsJsonAsync("/api/sessions", new { stationId, playerCount });
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("id").GetGuid();
    }

    public static Task CheckpointAsync(HttpClient client, Guid sessionId, DateTimeOffset at)
        => client.PostAsJsonAsync($"/api/sessions/{sessionId}/checkpoints", new { at });
}

internal static class TestTimes
{
    public static DateTimeOffset T(int year, int month, int day, int hour, int minute, int second = 0)
        => GameClub.Billing.TehranTime.At(year, month, day, hour, minute, second);
}

internal static class HttpClientExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient client, string url, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body),
        };
        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> PutAsJsonAsync<T>(this HttpClient client, string url, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        return client.SendAsync(request);
    }
}
