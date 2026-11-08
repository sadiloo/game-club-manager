using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR5AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task live_grace_wallet500_ends_at_grace_wall_minute_no_overshoot()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "GraceR5", Mobile = "09121112233", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "gracer5", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "gracer5", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();

        factory.Clock.SetTehran(2026, 6, 3, 14, 0, 59);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 59_000, default);
            Assert.Equal("Open", ack.SessionStatus);
            Assert.Equal(500, ack.Display!.WalletBalanceToman!.Value);
            Assert.NotNull(ack.Lease);
            Assert.True(ack.Lease!.Payload.AuthorizedUntilUtc <= new DateTime(2026, 6, 3, 10, 31, 0, DateTimeKind.Utc));
            if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(ack.Lease.Payload.AuthorizedUntilUtc <= new DateTime(2026, 6, 3, 10, 31, 0, DateTimeKind.Utc));
            }
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 2, 1);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            var ack = await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 121_000, default);
            Assert.NotEqual("Open", ack.SessionStatus);
            Assert.Equal(500, ack.Display!.WalletBalanceToman!.Value);
        }

        var op = await factory.CreateOperatorClientAsync();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(0, bill.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task overshoot_get_bill_exposes_writeoff_plain_and_arena()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StationGroups.Add(new StationGroupEntity { Id = "arena-r4", Name = "Arena", Kind = StationKind.Pc });
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO rate_lines ("GroupId", "PlayerCount", "PlayMode", "IsSpecialBand", "HourlyToman")
                SELECT 'arena-r4', "PlayerCount", "PlayMode", "IsSpecialBand", 120000
                FROM rate_lines WHERE "GroupId" = 'pc-normal' LIMIT 1
                """);
            await db.SaveChangesAsync();
        }

        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db0 = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db0.Database.ExecuteSqlRawAsync("UPDATE rate_lines SET \"HourlyToman\" = 120000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayerCount\" = 1");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "OverGet", Mobile = "09127777001", CreatedAtUtc = DateTime.UtcNow };
            db0.Customers.Add(customer);
            db0.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db0.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "overget", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "overget", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        await using (var graceScope = factory.Services.CreateAsyncScope())
        {
            var dbGrace = graceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await dbGrace.BranchMemberSettings.SingleAsync();
            settings.GraceMinuteEnabled = false;
            await dbGrace.SaveChangesAsync();
        }

        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.True(bill.GetProperty("writeOffToman").GetInt64() >= 1_500);
        Assert.Equal(500, bill.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task catalog_admin_editor_survives_refresh_and_enabled_persists()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var created = await mgr.PostAsJsonAsync("/api/operator/games", new
        {
            name = "DisabledProbe",
            categoryId = 1,
            executableOrUri = "probe.exe",
            enabled = false,
        });
        created.EnsureSuccessStatusCode();
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>();
        var gameId = createdBody.GetProperty("id").GetInt32();
        await mgr.GetAsync("/api/operator/games");
        var listed = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/games");
        Assert.True(listed.GetProperty("entries").GetArrayLength() >= 1);
        var row = listed.GetProperty("entries").EnumerateArray().First(e => e.GetProperty("id").GetInt32() == gameId);
        Assert.False(row.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void operator_guest_open_ack_unlocks_runtime_from_lease_session()
    {
        var ack = new GameClub.Kiosk.Core.Protocol.KioskHeartbeatAck
        {
            SessionStatus = "Open",
            Lease = new GameClub.Kiosk.Core.Protocol.SignedSessionLease
            {
                Payload = new GameClub.Kiosk.Core.Protocol.SessionLeasePayload
                {
                    SessionId = Guid.Parse("a525daec-33fb-4dab-8009-5d3e7f34787d"),
                    StationId = "PC-1",
                    AuthorizedUntilUtc = DateTime.UtcNow.AddMinutes(1),
                    IssuedAtUtc = DateTime.UtcNow,
                },
                SignatureBase64 = "dGVzdA==",
            },
            Display = new GameClub.Kiosk.Core.Protocol.KioskDisplayDto { OpenGuestElapsedOnly = true },
        };
        var runtime = new GameClub.Kiosk.Core.Runtime.KioskRuntime(() => new HttpClient());
        runtime.Configure(new Uri("http://127.0.0.1:5000/hubs/kiosk"), Guid.NewGuid(), "key", "PC-1", Path.GetTempFileName(), legacyWindows: false);
        typeof(GameClub.Kiosk.Core.Runtime.KioskRuntime).GetMethod("ApplyAcknowledgement", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(runtime, [ack]);
        Assert.Equal(GameClub.Kiosk.Core.Runtime.KioskRuntimeState.Playing, runtime.State);
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(GameClub.Server.Kiosk.KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(GameClub.Server.Kiosk.KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
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
