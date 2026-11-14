using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Lease;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR6AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task grace_wallet500_preview_collectible_zero_at_80k()
    {
        var sessionId = await SeedGraceWalletSessionAsync(80_000);
        var op = await factory.CreateOperatorClientAsync();
        var get = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(0, get.GetProperty("collectibleToman").GetInt64());
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 500L } },
        });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task grace_wallet500_preview_collectible_zero_at_60k()
    {
        var sessionId = await SeedGraceWalletSessionAsync(60_000);
        var op = await factory.CreateOperatorClientAsync();
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 500L } },
        });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task funded_remaining_48667_reports_2160_seconds_not_2220()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Funded48667", Mobile = "09120001001", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 50_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "fund48667", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "fund48667", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
            Assert.Equal(48_667, ack.Display!.WalletBalanceToman);
            Assert.Equal(2_160, ack.Display.EstimatedRemainingSeconds);
            Assert.NotNull(ack.Lease);
            var leaseSeconds = (ack.Lease!.Payload.AuthorizedUntilUtc - factory.Clock.UtcNow.UtcDateTime).TotalSeconds;
            Assert.InRange(leaseSeconds, 2_150, 2_170);
        }
    }

    [Fact]
    public async Task funded_remaining_100k_vip_reports_3240_seconds_not_3300()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-VIP", PilotSeed.PcVipGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 110000 WHERE \"GroupId\" = 'pc-vip' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Vip100k", Mobile = "09120001002", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 100_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "vip100k", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-VIP");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "vip100k", password = "pass", stationId = "PC-VIP" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 0, default);
            Assert.Equal(3_240, ack.Display!.EstimatedRemainingSeconds);
        }
    }

    [Fact]
    public async Task catalog_put_category_two_persists()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var created = await mgr.PostAsJsonAsync("/api/operator/games", new
        {
            name = "CatPutR6",
            categoryId = 1,
            executableOrUri = "cat.exe",
        });
        created.EnsureSuccessStatusCode();
        var gameId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var put = await mgr.PutAsJsonAsync($"/api/operator/games/{gameId}", new
        {
            name = "CatPutR6",
            categoryId = 2,
            enabled = true,
            executableOrUri = "cat.exe",
        });
        put.EnsureSuccessStatusCode();
        var listed = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/games");
        var row = listed.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("id").GetInt32() == gameId);
        Assert.Equal(2, row.GetProperty("categoryId").GetInt32());
    }

    [Fact]
    public async Task catalog_put_unknown_category_returns_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var created = await mgr.PostAsJsonAsync("/api/operator/games", new
        {
            name = "CatUnknown",
            categoryId = 1,
            executableOrUri = "cat.exe",
        });
        created.EnsureSuccessStatusCode();
        var gameId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var put = await mgr.PutAsJsonAsync($"/api/operator/games/{gameId}", new
        {
            name = "CatUnknown",
            categoryId = 99,
            enabled = true,
            executableOrUri = "cat.exe",
        });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadAsStringAsync();
        Assert.Contains("invalid_category", body, StringComparison.OrdinalIgnoreCase);
        var listed = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/games");
        var row = listed.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("id").GetInt32() == gameId);
        Assert.Equal(1, row.GetProperty("categoryId").GetInt32());
    }

    private async Task<Guid> SeedGraceWalletSessionAsync(long hourly)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE rate_lines SET \"HourlyToman\" = {0} WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1",
                hourly);
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "GracePrev", Mobile = "09129990001", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "graceprev", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "graceprev", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 2, 1);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 121_000, default);
        }

        return sessionId;
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
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }
}
