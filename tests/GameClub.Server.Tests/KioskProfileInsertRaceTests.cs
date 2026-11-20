using System.Data.Common;
using System.Net;
using GameClub.Billing;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskProfileInsertRaceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task first_login_on_fresh_station_survives_concurrent_profile_insert_and_starts_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-RACE";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var customer = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = "Race",
            Mobile = "09128888888",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customer.Id, "raceprof", "pass", default);

        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        using var gate = KioskProfileInsertRaceInterceptor.Activate(factory, stationId);

        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var login = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "raceprof", password = "pass", stationId });

        Assert.Equal(1, gate.Fired);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var sessionId = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();

        var openOnStation = await db.Sessions.CountAsync(
            s => s.Id == sessionId && s.StationId == stationId && s.Status == SessionStatus.Open);
        Assert.Equal(1, openOnStation);
        Assert.Equal(1, await db.StationKioskProfiles.CountAsync(p => p.StationId == stationId));
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

public sealed class KioskProfileInsertRaceGate(string stationId, string connectionString, GameClubWebApplicationFactory factory)
    : IDisposable
{
    public string StationId { get; } = stationId;

    public string ConnectionString { get; } = connectionString;

    public int Fired;

    public void Dispose()
    {
        if (ReferenceEquals(factory.ProfileInsertRaceGate, this))
        {
            factory.ProfileInsertRaceGate = null;
        }
    }
}

public sealed class KioskProfileInsertRaceInterceptor(GameClubWebApplicationFactory hostFactory) : DbCommandInterceptor
{
    public static KioskProfileInsertRaceGate Activate(GameClubWebApplicationFactory factory, string stationId)
    {
        var gate = new KioskProfileInsertRaceGate(stationId, factory.ConnectionString, factory);
        factory.ProfileInsertRaceGate = gate;
        return gate;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        TryObserveAndInject(command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await TryObserveAndInjectAsync(command, cancellationToken);
        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        TryObserveAndInject(command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await TryObserveAndInjectAsync(command, cancellationToken);
        return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    private void TryObserveAndInject(DbCommand command)
        => TryObserveAndInjectAsync(command, default).GetAwaiter().GetResult();

    private async Task TryObserveAndInjectAsync(DbCommand command, CancellationToken cancellationToken)
    {
        var gate = hostFactory.ProfileInsertRaceGate;
        if (gate is null)
        {
            return;
        }

        if (command.CommandText.IndexOf("station_kiosk_profiles", StringComparison.Ordinal) >= 0
            && command.CommandText.IndexOf("INSERT INTO", StringComparison.OrdinalIgnoreCase) >= 0
            && Interlocked.CompareExchange(ref gate.Fired, 1, 0) == 0)
        {
            await InsertCompetingProfileAsync(gate.ConnectionString, gate.StationId, cancellationToken);
        }
    }

    private static async Task InsertCompetingProfileAsync(string connectionString, string stationId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO station_kiosk_profiles ("StationId", "IsTestMode", "NearEndMinutesJson")
            VALUES (@sid, FALSE, '[10,5,1]')
            """,
            conn);
        cmd.Parameters.AddWithValue("sid", stationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class KioskProfileInsertRaceDbContextOptions(KioskProfileInsertRaceInterceptor interceptor)
    : IDbContextOptionsConfiguration<AppDbContext>
{
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.AddInterceptors(interceptor);
}

internal static class KioskProfileInsertRaceInjectionRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.TryAddSingleton<KioskProfileInsertRaceInterceptor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDbContextOptionsConfiguration<AppDbContext>, KioskProfileInsertRaceDbContextOptions>());
    }
}