using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>DEF-3 server: idempotent member re-login reuses the open session.</summary>
[Collection("server")]
public sealed class KioskLoginRetryAfterStallTests(GameClubWebApplicationFactory factory)
{
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

        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var first = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "staller", password = "pass", stationId });
        first.EnsureSuccessStatusCode();
        var firstSessionId = JsonDocument.Parse(await first.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();

        var second = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "staller", password = "pass", stationId });
        second.EnsureSuccessStatusCode();
        var secondSessionId = JsonDocument.Parse(await second.Content.ReadAsStringAsync())
            .RootElement.GetProperty("sessionId").GetGuid();

        Assert.Equal(firstSessionId, secondSessionId);
        Assert.Equal(1, await db.Sessions.CountAsync(
            s => s.StationId == stationId && s.Status == SessionStatus.Open));
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