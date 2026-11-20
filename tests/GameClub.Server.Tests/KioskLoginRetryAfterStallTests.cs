using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>DEF-3 server: client login timeout while server completes, retry attaches once.</summary>
[Collection("server")]
public sealed class KioskLoginRetryAfterStallTests(GameClubWebApplicationFactory factory)
{
    private static readonly TimeSpan KioskLoginHttpTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ServerLoginStall = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task retry_login_after_first_attach_reuses_single_open_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-STALL";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        var customer = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = "Stall",
            Mobile = "09121112222",
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await auth.SetCredentialAsync(customer.Id, "staller", "pass", default);

        using var stall = KioskLoginStallGate.Activate(factory, ServerLoginStall);
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        using var clientTimeout = new CancellationTokenSource(KioskLoginHttpTimeout);
        await Assert.ThrowsAnyAsync<TaskCanceledException>(async () =>
            await kiosk.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "staller", password = "pass", stationId },
                clientTimeout.Token));

        var openSessions = await db.Sessions
            .Where(s => s.StationId == stationId && s.Status == SessionStatus.Open)
            .Select(s => s.Id)
            .ToListAsync();
        var firstSessionId = Assert.Single(openSessions);

        var retryClient = CreateKioskClient(deviceId, deviceKey);
        var retry = await retryClient.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "staller", password = "pass", stationId });
        retry.EnsureSuccessStatusCode();
        var retrySessionId = JsonDocument.Parse(await retry.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(firstSessionId, retrySessionId);
        Assert.Equal(1, await db.Sessions.CountAsync(
            s => s.StationId == stationId && s.Status == SessionStatus.Open));
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey, TimeSpan? timeout = null)
    {
        var client = factory.CreateClient();
        if (timeout is not null)
        {
            client.Timeout = timeout.Value;
        }

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
            if (!context.Request.Path.StartsWithSegments("/api/kiosk/auth/login") || hostFactory.LoginStallGate is null)
            {
                await nextMiddleware(context);
                return;
            }

            var gate = hostFactory.LoginStallGate;
            var originalBody = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            await nextMiddleware(context);
            await Task.Delay(gate.ResponseDelayAfterLogin);
            buffer.Position = 0;
            context.Response.Body = originalBody;
            await buffer.CopyToAsync(originalBody);
        });
        next(application);
    };
}

public sealed class KioskLoginStallGate(GameClubWebApplicationFactory factory, TimeSpan responseDelayAfterLogin) : IDisposable
{
    public TimeSpan ResponseDelayAfterLogin { get; } = responseDelayAfterLogin;

    public static KioskLoginStallGate Activate(GameClubWebApplicationFactory factory, TimeSpan responseDelayAfterLogin)
    {
        var gate = new KioskLoginStallGate(factory, responseDelayAfterLogin);
        factory.LoginStallGate = gate;
        return gate;
    }

    public void Dispose()
    {
        if (ReferenceEquals(factory.LoginStallGate, this))
        {
            factory.LoginStallGate = null;
        }
    }
}