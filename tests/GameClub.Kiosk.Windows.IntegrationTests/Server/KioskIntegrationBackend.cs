#if NET10_0_OR_GREATER
using System;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

internal sealed class KioskIntegrationBackend
{
    private readonly GameClubWebApplicationFactory? _factory;
    private readonly string? _liveBaseUrl;
    private readonly string _apiKey;

    private KioskIntegrationBackend(GameClubWebApplicationFactory factory)
    {
        _factory = factory;
        _apiKey = "dev-key";
    }

    private KioskIntegrationBackend(Uri liveBaseUrl)
    {
        _liveBaseUrl = liveBaseUrl.ToString().TrimEnd('/');
        _apiKey = Environment.GetEnvironmentVariable("GAMECLUB_API_KEY") ?? "ci-test-key";
    }

    public static bool IsLiveMode => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL"));

    public static KioskIntegrationBackend Create(GameClubWebApplicationFactory? factory)
    {
        var live = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(live))
        {
            return new KioskIntegrationBackend(new Uri(live));
        }

        if (factory is null)
        {
            throw new InvalidOperationException("Factory required when CHECKPOINT_SERVER_URL is unset.");
        }

        return new KioskIntegrationBackend(factory);
    }

    public bool UsesMutableClock => _factory is not null;

    public async Task ResetAsync()
    {
        if (_factory is not null)
        {
            await S3TestHelpers.ResetAsync(_factory);
            return;
        }

        await LiveDatabaseReset.ResetAsync();
    }

    public HttpClient CreateApiClient()
    {
        if (_factory is not null)
        {
            return _factory.CreateClientWithKey();
        }

        var client = new HttpClient { BaseAddress = new Uri(_liveBaseUrl! + "/") };
        client.DefaultRequestHeaders.Add(ApiKeyMiddleware.HeaderName, _apiKey);
        return client;
    }

    public async Task<HttpClient> CreateOperatorClientAsync(string username = "manager", string pin = "1234")
    {
        if (_factory is not null)
        {
            return await _factory.CreateOperatorClientAsync(username, pin);
        }

        var client = new HttpClient { BaseAddress = new Uri(_liveBaseUrl! + "/") };
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username, pin });
        login.EnsureSuccessStatusCode();
        return client;
    }

    public Uri HubUri => _factory is not null
        ? new Uri(_factory.Server.BaseAddress!, "hubs/kiosk")
        : new Uri(_liveBaseUrl! + "/hubs/kiosk");

    public HttpMessageHandler? TestHubHandler => _factory?.Server.CreateHandler();

    public void SetTehran(int year, int month, int day, int hour, int minute, int second = 0)
    {
        if (_factory is null)
        {
            throw new InvalidOperationException("MutableClock is only available in factory mode.");
        }

        _factory.Clock.SetTehran(year, month, day, hour, minute, second);
    }

    public async Task<int> CountHeartbeatsAsync()
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().KioskHeartbeats.CountAsync();
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT COUNT(*)::int FROM kiosk_heartbeats""", conn);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public async Task<DateTime?> DeviceLastSeenUtcAsync(Guid deviceId)
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>().KioskDevices.SingleAsync(d => d.Id == deviceId);
            return device.LastSeenUtc;
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "LastSeenUtc" FROM kiosk_devices WHERE "Id" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", deviceId);
        var result = await cmd.ExecuteScalarAsync();
        if (result is null or DBNull)
        {
            return null;
        }

        return result is DateTime dt ? dt : Convert.ToDateTime(result);
    }

    public async Task BackdateSessionCheckpointAsync(Guid sessionId, TimeSpan ago)
    {
        var stamp = DateTime.UtcNow - ago;
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
            session.LastCheckpointUtc = stamp;
            await db.SaveChangesAsync();
            return;
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """UPDATE sessions SET "LastCheckpointUtc" = @at WHERE "Id" = @id""",
            conn);
        cmd.Parameters.AddWithValue("at", stamp);
        cmd.Parameters.AddWithValue("id", sessionId);
        await cmd.ExecuteNonQueryAsync();
    }

  public async Task BackdateOpenSessionForDeadPcSweepAsync(Guid sessionId)
    {
        var checkpoint = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-11), DateTimeKind.Utc);
        var started = DateTime.SpecifyKind(checkpoint.AddHours(-1), DateTimeKind.Utc);
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.SingleAsync(s => s.Id == sessionId);
            session.StartedAtUtc = started;
            session.LastCheckpointUtc = checkpoint;
            var segments = await db.Segments.Where(s => s.SessionId == sessionId).ToListAsync();
            foreach (var segment in segments)
            {
                segment.StartUtc = started;
            }

            await db.SaveChangesAsync();
            return;
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE sessions
            SET "StartedAtUtc" = @started, "LastCheckpointUtc" = @checkpoint
            WHERE "Id" = @id
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("started", started);
            cmd.Parameters.AddWithValue("checkpoint", checkpoint);
            cmd.Parameters.AddWithValue("id", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var segCmd = new NpgsqlCommand(
            """UPDATE play_segments SET "StartUtc" = @started WHERE "SessionId" = @id""",
            conn))
        {
            segCmd.Parameters.AddWithValue("started", started);
            segCmd.Parameters.AddWithValue("id", sessionId);
            await segCmd.ExecuteNonQueryAsync();
        }
    }

    public async Task<Guid> OpenSessionIdAsync()
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions
                .Where(s => s.Status == SessionStatus.Open)
                .Select(s => s.Id)
                .SingleAsync();
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "Id" FROM sessions WHERE "Status" = 'Open' LIMIT 1""",
            conn);
        return (Guid)(await cmd.ExecuteScalarAsync() ?? throw new InvalidOperationException("No open session."));
    }

    public async Task<long> WalletDebitTotalAsync(Guid customerId)
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().WalletLedger
                .Where(l => l.CustomerId == customerId && l.RealDeltaToman < 0)
                .SumAsync(l => l.RealDeltaToman);
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT COALESCE(SUM("RealDeltaToman"),0) FROM wallet_ledger WHERE "CustomerId" = @id AND "RealDeltaToman" < 0""",
            conn);
        cmd.Parameters.AddWithValue("id", customerId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task RegisterPcAsync(HttpClient api, string stationId, string groupId = PilotSeed.PcNormalGroupId)
        => await S3TestHelpers.RegisterPc(api, stationId, groupId);

    public async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairAsync(string stationId)
    {
        var op = await CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = (await codeBody.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString()!;
        var client = _factory is not null ? _factory.CreateClient() : new HttpClient { BaseAddress = new Uri(_liveBaseUrl! + "/") };
        var pair = await client.PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }

    public async Task<Guid> SeedCustomerWithCredentialAsync(string login, string password, long walletToman)
    {
        var op = await CreateOperatorClientAsync();
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = login, mobile = $"0912{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = walletToman });
        await op.PutAsJsonAsync($"/api/operator/customers/{customerId}/credential", new { loginName = login, password });
        return customerId;
    }

    public async Task AssertDevicePairedAsync(Guid deviceId, string stationId)
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>().KioskDevices.SingleAsync(d => d.Id == deviceId);
            Assert.Equal(stationId, device.StationId);
            Assert.NotEqual(default, device.PairedAtUtc);
            return;
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "StationId", "PairedAtUtc" IS NOT NULL FROM kiosk_devices WHERE "Id" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(stationId, reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
    }

    public async Task AssertCredentialLockedAsync(Guid customerId, bool locked)
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var cred = await scope.ServiceProvider.GetRequiredService<AppDbContext>().CustomerCredentials.SingleAsync(c => c.CustomerId == customerId);
            Assert.Equal(locked, cred.LockedUntilReset);
            return;
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """SELECT "LockedUntilReset" FROM customer_credentials WHERE "CustomerId" = @id""",
            conn);
        cmd.Parameters.AddWithValue("id", customerId);
        Assert.Equal(locked, Convert.ToBoolean(await cmd.ExecuteScalarAsync()));
    }

    public async Task<Guid> StartSessionAsync(string stationId, int playerCount = 1)
        => await S3TestHelpers.StartSession(CreateApiClient(), stationId, playerCount);

    public async Task<long> PendingBillCollectibleAsync()
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().PendingBills.SumAsync(b => b.CollectibleToman);
        }

        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""SELECT COALESCE(SUM("CollectibleToman"),0) FROM pending_bills""", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public HttpClient CreateKioskHttpClient(Guid deviceId, string deviceKey)
    {
        var client = _factory is not null ? _factory.CreateClient() : new HttpClient { BaseAddress = new Uri(_liveBaseUrl! + "/") };
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private static string ResolveConnectionString() =>
        Environment.GetEnvironmentVariable("GAMECLUB_TEST_DATABASE")
        ?? Environment.GetEnvironmentVariable("GAMECLUB_DATABASE")
        ?? throw new InvalidOperationException("GAMECLUB_DATABASE is not set.");
}
#endif
