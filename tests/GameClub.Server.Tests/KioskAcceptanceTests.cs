using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Lease;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task pair_invalid_code_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/kiosk/pair", new { code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task f6_lock_after_five_failed_logins_until_cu1_reset()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Ali", Mobile = "09120000001", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        var auth = scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>();
        await auth.SetCredentialAsync(customer.Id, "ali", "good-pass", default);

        var (deviceId, deviceKey, _) = await PairAsync("PC-1");
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        for (var i = 0; i < 5; i++)
        {
            var bad = await kiosk.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "ali", password = "wrong", stationId = "PC-1" });
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var locked = await kiosk.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "ali", password = "good-pass", stationId = "PC-1" });
        Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);

        await op.PutAsJsonAsync($"/api/operator/customers/{customer.Id}/credential", new { loginName = "ali", password = "new-good" });
        var ok = await kiosk.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "ali", password = "new-good", stationId = "PC-1" });
        ok.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task heartbeat_advances_checkpoint_each_minute()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairAsync("PC-1");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync("/api/operator/stations/PC-1/start", null);
        factory.Clock.SetTehran(2026, 6, 3, 14, 2, 5);
        using var scope = factory.Services.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
        var sessionId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await coordinator.OnHeartbeatAsync(deviceId, sessionId, 120_000, default);
        var session = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.SingleAsync();
        Assert.Equal(new DateTime(2026, 6, 3, 10, 32, 0, DateTimeKind.Utc), session.LastCheckpointUtc);
        _ = deviceKey;
    }

    [Fact]
    public async Task pc_bill_cancel_does_not_resume_play()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-1", PilotSeed.PcNormalGroupId);
        var op = await factory.CreateOperatorClientAsync();
        var start = await op.PostAsync("/api/operator/stations/PC-1/start", null);
        start.EnsureSuccessStatusCode();
        var sessionId = JsonDocument.Parse(await start.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);
        var cancel = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/cancel", null);
        cancel.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.Include(s => s.PendingBill).SingleAsync();
        Assert.Equal(SessionStatus.AwaitingPayment, session.Status);
        Assert.NotNull(session.PendingBill);
        Assert.True(session.EndedAtUtc.HasValue);
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairAsync(string stationId)
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
