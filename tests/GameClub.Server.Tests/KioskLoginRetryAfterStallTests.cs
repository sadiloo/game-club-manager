using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GameClub.Server.Tests;

/// <summary>DEF-3: attach after session-insert race; same-member retry after kiosk HTTP timeout window.</summary>
[Collection("server")]
public sealed class KioskLoginRetryAfterStallTests(GameClubWebApplicationFactory factory)
{
    /// <summary>WPF kiosk <see cref="KioskHostService"/> uses default <see cref="HttpClient.Timeout"/> (not set explicitly).</summary>
    private static readonly TimeSpan KioskLoginHttpTimeout = new HttpClient().Timeout;

    private const string RaceStationId = "PC-STALL";
    private const string RetryStationId = "PC-STALL-T";

    [Fact(Timeout = 240_000)]
    public async Task retry_login_after_first_attach_reuses_single_open_session()
    {
        KioskAttachPathTestSeam.InjectBeforeMemberStartAsync = null;
        if (factory.LoginStallGate is not null)
        {
            factory.LoginStallGate.Dispose();
            factory.LoginStallGate = null;
        }

        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), RaceStationId, PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), RetryStationId, PilotSeed.PcNormalGroupId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        const int raceParallel = 10;
        for (var i = 0; i < raceParallel; i++)
        {
            await SeedMemberAsync(db, auth, $"race{i}", $"091210000{i:00}");
        }

        await SeedMemberAsync(db, auth, "timeouter", "09121113344");

        var (raceDeviceId, raceDeviceKey, _) = await PairDeviceAsync(RaceStationId);
        InstallSlowSessionInsert(db);
        try
        {
            var raceTasks = Enumerable.Range(0, raceParallel).Select(i =>
            {
                var kiosk = CreateKioskClient(raceDeviceId, raceDeviceKey);
                return kiosk.PostAsJsonAsync(
                    "/api/kiosk/auth/login",
                    new { loginName = $"race{i}", password = "pass", stationId = RaceStationId });
            }).ToArray();

            var raceResponses = await Task.WhenAll(raceTasks);
            var serverErrors = raceResponses.Where(r => (int)r.StatusCode >= 500).ToArray();
            Assert.True(
                serverErrors.Length == 0,
                await DescribeUnexpectedStatusCodesAsync(
                    "race_parallel_login",
                    serverErrors));
            Assert.Equal(1, raceResponses.Count(r => r.StatusCode == HttpStatusCode.OK));
            foreach (var response in raceResponses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            {
                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
                Assert.Equal("station_busy", body.GetProperty("error").GetString());
            }

            Assert.Equal(1, await db.Sessions.CountAsync(
                s => s.StationId == RaceStationId && s.Status == SessionStatus.Open));
        }
        finally
        {
            RemoveSlowSessionInsert(db);
        }

        var (retryDeviceId, retryDeviceKey, _) = await PairDeviceAsync(RetryStationId);
        using var responseHold = KioskLoginStallGate.ActivateResponseHold(factory);
        var kiosk = CreateKioskClient(retryDeviceId, retryDeviceKey);
        kiosk.Timeout = Timeout.InfiniteTimeSpan;

        var slowLogin = kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "timeouter", password = "pass", stationId = RetryStationId });

        await responseHold.HandlerCommitted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await Task.Delay(KioskLoginHttpTimeout);

        var retryLogin = CreateKioskClient(retryDeviceId, retryDeviceKey).PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "timeouter", password = "pass", stationId = RetryStationId });

        var retryResponse = await retryLogin;
        responseHold.ReleaseResponse();

        var slowResponse = await slowLogin;
        var stallBodies = new[] { slowResponse, retryResponse }.Where(r => (int)r.StatusCode >= 500).ToArray();
        Assert.True(
            stallBodies.Length == 0,
            await DescribeUnexpectedStatusCodesAsync("stall_retry_login", stallBodies));
        Assert.True(
            slowResponse.IsSuccessStatusCode,
            await DescribeUnexpectedStatusAsync("slow_login", slowResponse));
        Assert.True(
            retryResponse.IsSuccessStatusCode,
            await DescribeUnexpectedStatusAsync("retry_login", retryResponse));

        var slowSessionId = JsonDocument.Parse(await slowResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();
        var retrySessionId = JsonDocument.Parse(await retryResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(slowSessionId, retrySessionId);
        Assert.Equal(1, await db.Sessions.CountAsync(
            s => s.StationId == RetryStationId && s.Status == SessionStatus.Open));
    }

    private static async Task<string> DescribeUnexpectedStatusAsync(string label, HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        return $"{label} status={(int)response.StatusCode} {response.StatusCode} body={body}";
    }

    private static async Task<string> DescribeUnexpectedStatusCodesAsync(
        string label,
        IReadOnlyList<HttpResponseMessage> responses)
    {
        var parts = new List<string> { label, $"count={responses.Count}" };
        foreach (var response in responses)
        {
            parts.Add(await DescribeUnexpectedStatusAsync("response", response));
        }

        return string.Join(" | ", parts);
    }

    private static async Task SeedMemberAsync(
        AppDbContext db,
        KioskCustomerAuthService auth,
        string login,
        string mobile)
    {
        var customer = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = login,
            Mobile = mobile,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customer.Id, login, "pass", default);
    }

    private static void InstallSlowSessionInsert(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            create or replace function kiosk_stall_sessions_insert() returns trigger language plpgsql as $$
            begin
                perform pg_sleep(4);
                return NEW;
            end $$;
            drop trigger if exists kiosk_stall_sessions_insert on sessions;
            create trigger kiosk_stall_sessions_insert before insert on sessions
              for each row execute function kiosk_stall_sessions_insert();
            """);
    }

    private static void RemoveSlowSessionInsert(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            drop trigger if exists kiosk_stall_sessions_insert on sessions;
            drop function if exists kiosk_stall_sessions_insert();
            """);
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairDeviceAsync(string stationId)
    {
        var op = await factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeBody.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var client = factory.CreateClient();
        var pair = await client.PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }
}

internal sealed class KioskLoginStallStartupFilter(GameClubWebApplicationFactory hostFactory) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
    {
        application.Use(async (context, nextMiddleware) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api/kiosk/auth/login"))
            {
                await nextMiddleware(context);
                return;
            }

            var gate = hostFactory.LoginStallGate;
            if (gate?.HoldResponseUntilReleased != true || !gate.TryEnterResponseHold())
            {
                await nextMiddleware(context);
                return;
            }

            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            await nextMiddleware(context);
            gate.HandlerCommitted.TrySetResult();
            await gate.WaitForReleaseAsync(context.RequestAborted);
            buffer.Position = 0;
            context.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody);
        });
        next(application);
    };
}

public sealed class KioskLoginStallGate : IDisposable
{
    private readonly GameClubWebApplicationFactory _factory;
    private readonly TaskCompletionSource _releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _holdClaimed;

    private KioskLoginStallGate(GameClubWebApplicationFactory factory, bool holdResponseUntilReleased)
    {
        _factory = factory;
        HoldResponseUntilReleased = holdResponseUntilReleased;
    }

    public bool HoldResponseUntilReleased { get; }

    public TaskCompletionSource HandlerCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryEnterResponseHold() => Interlocked.CompareExchange(ref _holdClaimed, 1, 0) == 0;

    public static KioskLoginStallGate ActivateResponseHold(GameClubWebApplicationFactory factory)
    {
        var gate = new KioskLoginStallGate(factory, holdResponseUntilReleased: true);
        factory.LoginStallGate = gate;
        return gate;
    }

    public Task WaitForReleaseAsync(CancellationToken ct) => _releaseResponse.Task.WaitAsync(ct);

    public void ReleaseResponse() => _releaseResponse.TrySetResult();

    public void Dispose()
    {
        if (ReferenceEquals(_factory.LoginStallGate, this))
        {
            _factory.LoginStallGate = null;
        }

        _releaseResponse.TrySetResult();
    }
}

internal static class PostgresTestFaults
{
    public static DbUpdateException ActiveSessionUniqueViolation()
        => Wrap(PostgresErrors.ActiveSessionIndexName, PostgresErrorCodes.UniqueViolation);

    public static DbUpdateException SerializationFailure()
        => Wrap("n/a", PostgresErrorCodes.SerializationFailure);

    public static DbUpdateException UniqueOnConstraint(string constraintName)
        => Wrap(constraintName, PostgresErrorCodes.UniqueViolation);

    public static PostgresException? FindPostgres(Exception ex, string sqlState)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg && pg.SqlState == sqlState)
            {
                return pg;
            }
        }

        return null;
    }

    private static DbUpdateException Wrap(string constraintName, string sqlState)
    {
        var pg = new PostgresException(
            messageText: "test fault",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: sqlState,
            constraintName: constraintName == "n/a" ? null : constraintName);
        return new DbUpdateException("injected postgres fault", pg);
    }
}