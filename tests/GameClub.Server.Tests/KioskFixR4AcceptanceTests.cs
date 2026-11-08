using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using static GameClub.Server.Operator.CatalogSeed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR4AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task overshoot_wallet500_preview_collectible_500_with_writeoff_field()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 500L } } });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(500, body.GetProperty("collectibleToman").GetInt64());
        Assert.True(body.GetProperty("writeOffToman").GetInt64() >= 1_500);
    }

    [Fact]
    public async Task overshoot_pc_plus_buffet_totals_15500()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.PendingBill).FirstAsync(s => s.Id == sessionId);
            var calculator = scope.ServiceProvider.GetRequiredService<BillCalculator>();
            var saleLines = await db.OpenSales.AsNoTracking()
                .Include(o => o.Lines)
                .Where(o => o.SessionId == sessionId)
                .SelectMany(o => o.Lines)
                .ToListAsync();
            var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
            Assert.Equal(15_500, bill.GetProperty("collectibleToman").GetInt64());
            Assert.Equal(15_500, session.PendingBill!.CollectibleToman);
            Assert.True(bill.GetProperty("writeOffToman").GetInt64() >= 1_500);
        }
    }

    [Fact]
    public async Task overshoot_arbitrary_pc_kind_group_audits()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StationGroups.Add(new StationGroupEntity { Id = "r3-overshoot", Name = "Arena", Kind = StationKind.Pc });
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO rate_lines ("GroupId", "PlayerCount", "PlayMode", "IsSpecialBand", "HourlyToman")
                SELECT 'r3-overshoot', "PlayerCount", "PlayMode", "IsSpecialBand", 120000
                FROM rate_lines WHERE "GroupId" = 'pc-normal' LIMIT 1
                """);
            await db.SaveChangesAsync();
        }

        var (sessionId, op) = await CreateOvershootSessionCoreAsync(
            graceOff: true,
            stationId: "PC-ARENA",
            groupId: "r3-overshoot",
            reset: false);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.True(bill.GetProperty("writeOffToman").GetInt64() >= 1_500);
        Assert.Equal(500, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal("pc_overshoot", bill.GetProperty("overshootAlert").GetProperty("code").GetString());
    }

    [Fact]
    public async Task catalog_admin_reports_entries_length()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await DatabaseSeed.EnsureAsync(db);
        }

        (await mgr.PostAsJsonAsync("/api/operator/games", new
        {
            name = "Probe",
            categoryId = 1,
            executableOrUri = "probe.exe",
        })).EnsureSuccessStatusCode();
        var listed = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/games");
        var entries = listed.GetProperty("entries");
        Assert.True(entries.GetArrayLength() > 0);
        Assert.True(listed.GetProperty("catalogVersion").GetInt64() >= 0);
    }

    [Fact]
    public void kiosk_host_wires_heartbeat_display_callback()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var host = File.ReadAllText(Path.Combine(repoRoot, "src", "GameClub.Kiosk", "Services", "KioskHostService.cs"));
        Assert.Contains("HeartbeatAcknowledged += OnHeartbeatAcknowledged", host);
        Assert.Contains("ApplyHeartbeatDisplay(ack.Display)", host);
    }

    private Task<(Guid SessionId, HttpClient Op)> CreateOvershootSessionAsync(
        bool graceOff,
        string stationId = "PC-1",
        string groupId = PilotSeed.PcNormalGroupId)
        => CreateOvershootSessionCoreAsync(graceOff, stationId, groupId, reset: true);

    private async Task<(Guid SessionId, HttpClient Op)> CreateOvershootSessionCoreAsync(
        bool graceOff,
        string stationId = "PC-1",
        string groupId = PilotSeed.PcNormalGroupId,
        bool reset = false)
    {
        if (reset)
        {
            await S3TestHelpers.ResetAsync(factory);
        }
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, groupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db0 = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db0.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE rate_lines SET "HourlyToman" = 120000 WHERE "GroupId" = {groupId} AND "PlayerCount" = 1""");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Over", Mobile = "09126666661", CreatedAtUtc = DateTime.UtcNow };
            db0.Customers.Add(customer);
            db0.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db0.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "over4", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "over4", password = "pass", stationId })
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
            sessionId = await lookup.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
            var coordinator = lookup.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>();
            factory.Clock.SetTehran(2026, 6, 3, 14, 1);
            await coordinator.OnHeartbeatAsync(deviceId, sessionId, 60_000, default);
            factory.Clock.SetTehran(2026, 6, 3, 14, 2);
            await coordinator.OnHeartbeatAsync(deviceId, sessionId, 120_000, default);
        }

        var op = await factory.CreateOperatorClientAsync();
        return (sessionId, op);
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
