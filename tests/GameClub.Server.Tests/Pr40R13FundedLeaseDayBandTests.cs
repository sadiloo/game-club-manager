using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

/// <summary>R13 — funded online day-band lease minutes must match hand-computed wallet at literal 80_000 toman/h.</summary>
[Collection("server")]
public sealed class Pr40R13FundedLeaseDayBandTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task online_session_wallet_at_80000_day_band_lease_authorizes_hand_computed_minutes()
    {
        const long hourlyToman = 80_000L;
        const long walletToman = 160_000L;
        const int expectedFundedMinutes = 120;

        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-R13-FUND", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity
            {
                Id = Guid.NewGuid(),
                Name = "R13Funded",
                Mobile = "09120009999",
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = walletToman });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "r13fund", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-R13-FUND");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "r13fund", password = "pass", stationId = "PC-R13-FUND" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Sessions.Select(s => s.Id).SingleAsync();

        await using var hbScope = factory.Services.CreateAsyncScope();
        var ack = await hbScope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
            .OnHeartbeatAsync(deviceId, sessionId, 60_000, default);

        Assert.NotNull(ack.Lease);
        var authorizedMinutes = (ack.Lease!.Payload.AuthorizedUntilUtc - factory.Clock.UtcNow.UtcDateTime).TotalMinutes;
        Assert.InRange(authorizedMinutes, expectedFundedMinutes - 1, expectedFundedMinutes + 1);
        const int expectedFundedSeconds = expectedFundedMinutes * 60;
        Assert.NotNull(ack.Display!.EstimatedRemainingSeconds);
        Assert.InRange(ack.Display.EstimatedRemainingSeconds!.Value, expectedFundedSeconds - 60, expectedFundedSeconds + 5);
        _ = hourlyToman;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string LeasePem)> PairDeviceAsync(string stationId)
    {
        var op = await factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeBody.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }
}