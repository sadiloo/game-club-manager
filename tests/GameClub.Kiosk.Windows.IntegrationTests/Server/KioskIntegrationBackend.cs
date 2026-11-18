#if NET10_0_OR_GREATER
using System;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
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

    public static KioskIntegrationBackend CreateForBaseUrl(string baseUrl)
        => new KioskIntegrationBackend(new Uri(baseUrl.TrimEnd('/')));

    public bool UsesMutableClock => _factory is not null;

    public async Task ResetAsync()
    {
        if (_factory is not null)
        {
            await S3TestHelpers.ResetAsync(_factory);
            return;
        }

        await LiveDatabaseMigration.EnsureMigratedAsync();
        await LiveDatabaseReset.ResetAsync();
    }

    public static bool UsesCiManagedServer =>
        IsLiveMode && int.TryParse(Environment.GetEnvironmentVariable("GAMECLUB_SERVER_PID"), out _);

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
        var started = DateTime.SpecifyKind(S3TestHelpers.T(2026, 6, 3, 14, 0).UtcDateTime, DateTimeKind.Utc);
        var checkpoint = DateTime.SpecifyKind(S3TestHelpers.T(2026, 6, 3, 15, 0).UtcDateTime, DateTimeKind.Utc);
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
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            """
            UPDATE sessions
            SET "StartedAtUtc" = @started, "LastCheckpointUtc" = @checkpoint
            WHERE "Id" = @id
            """,
            conn,
            tx))
        {
            cmd.Parameters.AddWithValue("started", started);
            cmd.Parameters.AddWithValue("checkpoint", checkpoint);
            cmd.Parameters.AddWithValue("id", sessionId);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var segCmd = new NpgsqlCommand(
            """UPDATE play_segments SET "StartUtc" = @started WHERE "SessionId" = @id""",
            conn,
            tx))
        {
            segCmd.Parameters.AddWithValue("started", started);
            segCmd.Parameters.AddWithValue("id", sessionId);
            await segCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
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

    public async Task SetPcNormalOfflineRate40000Async()
        => await PutPcNormalOfflineRateAsync(40_000L);

    public async Task SetPcNormalOfflineRate80000WithZeroSurchargeAsync()
        => await PutPcNormalOfflineRateAsync(PilotSeed.PcNormalHourly);

    private async Task PutPcNormalOfflineRateAsync(long hourlyToman)
    {
        if (_factory is null)
        {
            throw new InvalidOperationException("Factory required for pc-rates setup.");
        }
        var mgr = await _factory.CreateOperatorClientAsync("manager", "1234");
        var put = await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new
            {
                onlineDay = PilotSeed.PcNormalHourly,
                onlineNight = PilotSeed.PcNormalSpecialHourly,
                offlineDay = hourlyToman,
                offlineNight = hourlyToman,
            });
        await put.EnsureSuccessStatusCodeWithBodyAsync($"PUT /api/operator/pc-rates (offline {hourlyToman}/h)");
    }

    public async Task<G2BillingEvidence> CollectG2EvidenceAsync(Guid sessionId, double oraclePoweredSpanSeconds)
    {
        if (_factory is not null)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var checkpointCount = await db.Checkpoints.CountAsync(c => c.SessionId == sessionId);
            var factoryOfflineAuditCount = await db.AuditLog.CountAsync(a => a.Action == "kiosk.offline_segment");
            var checkpointMinMax = await db.Checkpoints
                .Where(c => c.SessionId == sessionId)
                .Select(c => new { c.AtUtc })
                .ToListAsync();
            var rateLine = await db.RateLines.SingleAsync(r =>
                r.GroupId == PilotSeed.PcNormalGroupId
                && r.PlayMode == "Online"
                && r.PlayerCount == null
                && !r.IsSpecialBand);
            var factoryBillTotal = await db.PendingBills
                .Where(b => b.SessionId == sessionId)
                .SumAsync(b => b.CollectibleToman);
            return G2BillingEvidence.Build(
                sessionId,
                checkpointCount,
                factoryOfflineAuditCount,
                checkpointMinMax.Select(c => c.AtUtc),
                rateLine.HourlyToman,
                factoryBillTotal,
                oraclePoweredSpanSeconds);
        }
        else
        {
        await using var conn = new NpgsqlConnection(ResolveConnectionString());
        await conn.OpenAsync();
        int checkpoints;
        await using (var cmd = new NpgsqlCommand("""SELECT COUNT(*)::int FROM checkpoints WHERE "SessionId" = @id""", conn))
        {
            cmd.Parameters.AddWithValue("id", sessionId);
            checkpoints = (int)(await cmd.ExecuteScalarAsync() ?? 0);
        }

        int offlineAuditCount;
        await using (var cmd = new NpgsqlCommand("""SELECT COUNT(*)::int FROM audit_log WHERE "Action" = 'kiosk.offline_segment'""", conn))
        {
            offlineAuditCount = (int)(await cmd.ExecuteScalarAsync() ?? 0);
        }

        var ats = new List<DateTime>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT "AtUtc" FROM checkpoints WHERE "SessionId" = @id ORDER BY "AtUtc"
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("id", sessionId);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ats.Add(reader.GetDateTime(0));
            }
        }

        long hourly;
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT "HourlyToman" FROM rate_lines
            WHERE "GroupId" = @group AND "PlayMode" = 'Offline' AND "PlayerCount" = 1 AND "IsSpecialBand" = false
            """,
            conn))
        {
            cmd.Parameters.AddWithValue("group", PilotSeed.PcNormalGroupId);
            hourly = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        long billTotal;
        await using (var cmd = new NpgsqlCommand(
            """SELECT COALESCE(SUM("CollectibleToman"),0) FROM pending_bills WHERE "SessionId" = @id""",
            conn))
        {
            cmd.Parameters.AddWithValue("id", sessionId);
            billTotal = Convert.ToInt64(await cmd.ExecuteScalarAsync() ?? 0L);
        }

        return G2BillingEvidence.Build(sessionId, checkpoints, offlineAuditCount, ats, hourly, billTotal, oraclePoweredSpanSeconds);
        }
    }

    public async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairAsync(string stationId)
    {
        var op = await CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        await codeBody.EnsureSuccessStatusCodeWithBodyAsync($"POST /api/operator/stations/{stationId}/pairing-code");
        var code = (await codeBody.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString()!;
        var client = _factory is not null ? _factory.CreateClient() : new HttpClient { BaseAddress = new Uri(_liveBaseUrl! + "/") };
        var pair = await client.PostAsJsonAsync("/api/kiosk/pair", new { code });
        await pair.EnsureSuccessStatusCodeWithBodyAsync("POST /api/kiosk/pair");
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
            new { method = "Cash", amountToman = walletToman, commandId = Guid.NewGuid()});
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

internal sealed record G2BillingEvidence(
    Guid SessionId,
    int CheckpointCount,
    int OfflineSegmentAuditCount,
    string OfflineSegmentMinuteSpan,
    long PcNormalOfflineHourlyToman,
    long PendingBillCollectibleToman,
    int ExpectedBillableMinutes,
    long ExpectedCollectibleToman,
    int DuplicateCheckpointMinuteBuckets)
{
    public static G2BillingEvidence Build(
        Guid sessionId,
        int checkpointCount,
        int offlineAudits,
        IEnumerable<DateTime> checkpointTimes,
        long hourlyToman,
        long bill,
        double oraclePoweredSpanSeconds)
    {
        var ordered = checkpointTimes.OrderBy(t => t).ToList();
        var span = ordered.Count == 0
            ? "no-checkpoints"
            : $"{ordered[0]:O}..{ordered[^1]:O} ({ordered.Count} rows)";
        var measuredSeconds = Math.Max(0, oraclePoweredSpanSeconds);
        var expectedMinutes = (int)Math.Floor(measuredSeconds / 60.0);
        var expectedCollectible = expectedMinutes * hourlyToman / 60;
        var minuteBuckets = ordered
            .Select(t => new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, DateTimeKind.Utc))
            .ToList();
        var duplicateMinuteBuckets = minuteBuckets
            .GroupBy(b => b)
            .Count(g => g.Count() > 1);
        return new(
            sessionId,
            checkpointCount,
            offlineAudits,
            span,
            hourlyToman,
            bill,
            expectedMinutes,
            expectedCollectible,
            duplicateMinuteBuckets);
    }

    public string ToDumpLine()
        => new StringBuilder()
            .Append("session=").Append(SessionId)
            .Append("; checkpoints=").Append(CheckpointCount)
            .Append("; offline_segment_audit=").Append(OfflineSegmentAuditCount)
            .Append("; checkpoint_span=").Append(OfflineSegmentMinuteSpan)
            .Append("; pc_normal_offline_rate_per_hour=").Append(PcNormalOfflineHourlyToman)
            .Append("; pending_bill_collectible=").Append(PendingBillCollectibleToman)
            .Append("; g2_expected_minutes=").Append(ExpectedBillableMinutes)
            .Append("; g2_expected_collectible=").Append(ExpectedCollectibleToman)
            .Append("; g2_duplicate_checkpoint_minute_buckets=").Append(DuplicateCheckpointMinuteBuckets)
            .ToString();
}
#endif
