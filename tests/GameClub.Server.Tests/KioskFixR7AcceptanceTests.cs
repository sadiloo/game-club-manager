using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR7AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task partial_grace_wallet1333_preview_stays_1333()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var get = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(1_333, get.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task partial_grace_wallet1333_post_preview_stays_1333()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_333L } },
        });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1_333, body.GetProperty("collectibleToman").GetInt64());
        Assert.NotEqual(6_333, body.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task partial_grace_wallet1333_confirm_retains_500()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_333L } },
        });
        confirm.EnsureSuccessStatusCode();
        var customerId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Sessions.Where(s => s.Id == sessionId).Select(s => s.CustomerId).SingleAsync();
        var wallet = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .CustomerMembers.Where(m => m.CustomerId == customerId).Select(m => m.RealToman).SingleAsync();
        Assert.Equal(500, wallet);
    }

    [Fact]
    public async Task settled_overshoot_plain_invoice_exposes_1500_burnt_line()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 500L } },
        });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(500, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_500, inv.GetProperty("writeOffToman").GetInt64());
    }

    [Fact]
    public async Task settled_overshoot_buffet_invoice_exposes_1500_burnt_line()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "Cash", amountToman = 15_000L },
            },
        });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(15_500, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_500, inv.GetProperty("writeOffToman").GetInt64());
    }

    private async Task<Guid> SeedPartialGraceSessionAsync()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "PartialGrace", Mobile = "09128881333", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 1_833 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "partial1333", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "partial1333", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 2);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 120_000, default);
        }

        return sessionId;
    }

    private async Task<(Guid SessionId, HttpClient Op)> CreateOvershootSessionAsync(bool graceOff)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db0 = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db0.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE rate_lines SET "HourlyToman" = 60000 WHERE "GroupId" = 'pc-normal' AND "PlayerCount" = 1""");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "OverR7", Mobile = "09126666333", CreatedAtUtc = DateTime.UtcNow };
            db0.Customers.Add(customer);
            db0.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db0.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "overr7", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "overr7", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        if (graceOff)
        {
            await using var graceScope = factory.Services.CreateAsyncScope();
            var dbGrace = graceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await dbGrace.BranchMemberSettings.SingleAsync();
            settings.GraceMinuteEnabled = false;
            await dbGrace.SaveChangesAsync();
        }

        Guid sessionId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<AppDbContext>();
            sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
            var session = await db.Sessions.Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstAsync(s => s.Id == sessionId);
            session.StartedAtUtc = factory.Clock.UtcNow.UtcDateTime;
            session.LastCheckpointUtc = session.StartedAtUtc.AddMinutes(2);
            session.Segments.Clear();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = session.StartedAtUtc,
                EndUtc = session.StartedAtUtc.AddMinutes(2),
                StationId = session.StationId,
                GroupId = session.Station!.GroupId,
                Mode = PlayMode.Offline,
                PlayerCount = 1,
                Kind = StationKind.Pc,
            });
            await db.SaveChangesAsync();
        }

        var op = await factory.CreateOperatorClientAsync();
        return (sessionId, op);
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
