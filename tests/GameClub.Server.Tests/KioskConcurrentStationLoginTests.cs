using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>DEF-2: concurrent different-customer logins on one PC station.</summary>
[Collection("server")]
public sealed class KioskConcurrentStationLoginTests(GameClubWebApplicationFactory factory)
{
    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task parallel_different_customer_logins_never_500_single_open_session_rest_station_busy(int parallelCount)
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-CONC";
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        for (var i = 0; i < parallelCount; i++)
        {
            var customer = new CustomerEntity
            {
                Id = Guid.NewGuid(),
                Name = $"C{i}",
                Mobile = $"091210000{i:00}",
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
            await db.SaveChangesAsync();
            await auth.SetCredentialAsync(customer.Id, $"user{i}", "pass", default);
        }

        var tasks = Enumerable.Range(0, parallelCount).Select(async i =>
        {
            var kiosk = CreateKioskClient(deviceId, deviceKey);
            return await kiosk.PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = $"user{i}", password = "pass", stationId });
        }).ToArray();

        var responses = await Task.WhenAll(tasks);
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);

        var okCount = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, okCount);

        foreach (var response in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("station_busy", body.GetProperty("error").GetString());
        }

        var openSessions = await db.Sessions.CountAsync(
            s => s.StationId == stationId && s.Status == SessionStatus.Open);
        Assert.Equal(1, openSessions);
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