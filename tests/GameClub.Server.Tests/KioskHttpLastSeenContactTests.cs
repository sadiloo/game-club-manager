using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("pr52-r4-http-lastseen")]
public sealed class KioskHttpLastSeenContactTests(Pr52R4HttpLastSeenWebApplicationFactory factory)
{
    [Fact]
    public async Task open_free_use_http_only_contact_updates_grid_and_report_duration_across_clock_steps()
    {
        await S3TestHelpers.ResetAsync(factory);
        var baseTime = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        factory.Clock.SetUtc(baseTime);

        const string stationId = "PC-FU-HTTP";
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        DateTime startedAtUtc;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
            startedAtUtc = factory.Clock.UtcNow.UtcDateTime;
            await svc.StartFreeUseAsync(stationId, deviceId, "operator", default);
        }

        var kiosk = CreateKioskClient(deviceId, deviceKey);

        factory.Clock.SetUtc(baseTime.AddSeconds(60));
        var contact1Utc = factory.Clock.UtcNow.UtcDateTime;
        (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();

        factory.Clock.SetUtc(baseTime.AddSeconds(120));
        var contact2Utc = factory.Clock.UtcNow.UtcDateTime;
        (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var device = await scope.ServiceProvider.GetRequiredService<AppDbContext>().KioskDevices.AsNoTracking()
                .SingleAsync(d => d.Id == deviceId);
            Assert.Equal(contact2Utc, device.LastSeenUtc);
        }

        var op = await factory.CreateOperatorClientAsync();
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = grid.EnumerateArray().Single(e => e.GetProperty("id").GetString() == stationId);
        Assert.Equal("FreeUse", tile.GetProperty("status").GetString());
        var gridFreeUse = tile.GetProperty("freeUse");
        Assert.False(gridFreeUse.GetProperty("noContact").GetBoolean());
        Assert.Equal(contact2Utc, gridFreeUse.GetProperty("lastContactUtc").GetDateTime());

        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var fromUtc = baseTime.UtcDateTime.AddDays(-1).ToString("O");
        var toUtc = baseTime.UtcDateTime.AddDays(1).ToString("O");
        var report = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/operator/reports/free-use?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}");
        var row = report.GetProperty("rows").EnumerateArray()
            .Single(e => e.GetProperty("stationId").GetString() == stationId);
        Assert.False(row.GetProperty("noContact").GetBoolean());
        Assert.Equal(contact2Utc, row.GetProperty("lastContactUtc").GetDateTime());
        Assert.Equal((long)(contact2Utc - startedAtUtc).TotalSeconds, row.GetProperty("durationSeconds").GetInt64());

        factory.Clock.SetUtc(baseTime.AddSeconds(120 + 240));
        var staleGrid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var staleTile = staleGrid.EnumerateArray().Single(e => e.GetProperty("id").GetString() == stationId);
        var staleFu = staleTile.GetProperty("freeUse");
        Assert.True(staleFu.GetProperty("noContact").GetBoolean());
        Assert.Equal(contact2Utc, staleFu.GetProperty("lastContactUtc").GetDateTime());

        var staleReport = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/operator/reports/free-use?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}");
        var staleRow = staleReport.GetProperty("rows").EnumerateArray()
            .Single(e => e.GetProperty("stationId").GetString() == stationId);
        Assert.True(staleRow.GetProperty("noContact").GetBoolean());
        Assert.Equal((long)(contact2Utc - startedAtUtc).TotalSeconds, staleRow.GetProperty("durationSeconds").GetInt64());
        Assert.Equal(contact2Utc, staleRow.GetProperty("lastContactUtc").GetDateTime());
        Assert.True(contact2Utc > contact1Utc);
    }

    /// <summary>
    /// R10f item 6 (rr4 L-10): the reset must clear kiosk_free_uses, otherwise a second run of the free-use scenario on
    /// the SAME database sees the earlier run's free-use row. Runs the scenario three times on this collection's database.
    /// </summary>
    [Fact]
    public async Task open_free_use_scenario_passes_three_times_on_the_same_database()
    {
        for (var run = 1; run <= 3; run++)
        {
            await open_free_use_http_only_contact_updates_grid_and_report_duration_across_clock_steps();
        }
    }

    [Fact]
    public async Task paid_kiosk_session_bill_and_lease_match_with_or_without_http_last_seen_stamps()
    {
        const string stationId = "PC-PARITY";
        var withHttp = await RunPaidKioskSessionAsync(interleaveHttpContact: true, stationId);
        var withoutHttp = await RunPaidKioskSessionAsync(interleaveHttpContact: false, stationId + "-B");
        Assert.Equal(withoutHttp.Steps, withHttp.Steps);
        var withBill = BillFingerprint(withoutHttp.Bill);
        var httpBill = BillFingerprint(withHttp.Bill);
        Assert.Equal(withBill, httpBill);
        Assert.Equal(KioskPaidSessionMainOracle.WithoutHttpBillFingerprint, withBill);
        Assert.Equal(KioskPaidSessionMainOracle.WithoutHttpBillFingerprint, httpBill);
    }

    [Fact]
    public async Task last_seen_http_write_throttled_to_once_per_thirty_seconds_per_device()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-THROTTLE";
        var baseTime = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        factory.Clock.SetUtc(baseTime);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        var kiosk = CreateKioskClient(deviceId, deviceKey);

        factory.Clock.SetUtc(baseTime.AddSeconds(10));
        (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();
        var firstSeen = await ReadLastSeenAsync(deviceId);

        factory.Clock.SetUtc(baseTime.AddSeconds(25));
        (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();
        var secondSeen = await ReadLastSeenAsync(deviceId);
        Assert.Equal(firstSeen, secondSeen);

        factory.Clock.SetUtc(baseTime.AddSeconds(41));
        (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();
        var thirdSeen = await ReadLastSeenAsync(deviceId);
        Assert.True(thirdSeen > firstSeen);
    }

    private async Task<PaidKioskSessionOutcome> RunPaidKioskSessionAsync(bool interleaveHttpContact, string stationId)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), stationId, PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE rate_lines SET "HourlyToman" = 80000 WHERE "GroupId" = 'pc-normal' AND "PlayerCount" = 1""");
            var customer = new CustomerEntity
            {
                Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-111122223333"),
                Name = "Parity",
                Mobile = "09120009999",
                CreatedAtUtc = DateTime.UtcNow,
            };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 240_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "parity", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 10, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var login = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "parity", password = "pass", stationId });
        login.EnsureSuccessStatusCode();
        var sessionId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

        var steps = new List<LeaseStepOutcome>();

        async Task HeartbeatAtAsync(int minute, long clientMonotonicMs)
        {
            factory.Clock.SetTehran(2026, 6, 10, 14, minute);
            await using var hbScope = factory.Services.CreateAsyncScope();
            var ack = await hbScope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, clientMonotonicMs, default);
            steps.Add(LeaseStepOutcome.FromAck(ack));
        }

        await HeartbeatAtAsync(15, 900_000);
        if (interleaveHttpContact)
        {
            (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/config")).EnsureSuccessStatusCode();
        }

        await HeartbeatAtAsync(30, 1_800_000);
        if (interleaveHttpContact)
        {
            (await kiosk.GetAsync($"/api/kiosk/stations/{stationId}/active-session")).EnsureSuccessStatusCode();
        }

        await HeartbeatAtAsync(45, 2_700_000);

        var op = await factory.CreateOperatorClientAsync();
        factory.Clock.SetTehran(2026, 6, 10, 14, 45);
        var forceLogout = await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, forceLogout.StatusCode);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        return new PaidKioskSessionOutcome(steps, BillOutcome.FromJson(bill));
    }

    private async Task<DateTime?> ReadLastSeenAsync(Guid deviceId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().KioskDevices.AsNoTracking()
            .Where(d => d.Id == deviceId)
            .Select(d => d.LastSeenUtc)
            .SingleAsync();
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

    private sealed record PaidKioskSessionOutcome(
        IReadOnlyList<LeaseStepOutcome> Steps,
        BillOutcome Bill);

    private readonly record struct LeaseStepOutcome(
        DateTime AuthorizedUntilUtc,
        DateTime MaxBillableCheckpointUtc,
        string PrepaidKind,
        string WalletDigest,
        int LeaseVersion,
        string SessionStatus,
        long? WalletBalanceToman,
        long? EstimatedRemainingSeconds)
    {
        public static LeaseStepOutcome FromAck(KioskHeartbeatAck ack)
        {
            var lease = ack.Lease ?? throw new InvalidOperationException("Expected lease on funded session heartbeat.");
            var p = lease.Payload;
            return new LeaseStepOutcome(
                p.AuthorizedUntilUtc,
                p.MaxBillableCheckpointUtc,
                p.PrepaidKind,
                p.WalletDigest,
                p.LeaseVersion,
                ack.SessionStatus,
                ack.Display?.WalletBalanceToman,
                ack.Display?.EstimatedRemainingSeconds);
        }
    }

    private readonly record struct BillOutcome(
        long UnroundedPayableToman,
        long RoundingToman,
        long CollectibleToman,
        long TimeGrossToman,
        long BuffetGrossToman,
        long ServicesGrossToman,
        long LevelDiscountToman,
        long BirthdayDiscountToman,
        long WriteOffToman,
        DateTime FrozenAtUtc,
        string? StopReason,
        IReadOnlyList<BillSegmentOutcome> Segments,
        IReadOnlyList<BillSaleLineOutcome> SaleLines)
    {
        public static BillOutcome FromJson(JsonElement bill)
        {
            var segments = bill.GetProperty("segments").EnumerateArray()
                .Select(s => new BillSegmentOutcome(
                    s.GetProperty("segmentKey").GetString()!,
                    s.GetProperty("playerCount").GetInt32(),
                    s.GetProperty("mode").GetString()!,
                    s.GetProperty("hourlyToman").GetInt64(),
                    s.GetProperty("startUtc").GetDateTime(),
                    s.GetProperty("endUtc").GetDateTime(),
                    s.GetProperty("displayLine").GetString()!))
                .OrderBy(s => s.StartUtc)
                .ToList();
            var saleLines = bill.TryGetProperty("saleLines", out var sl) && sl.ValueKind == JsonValueKind.Array
                ? sl.EnumerateArray()
                    .Select(l => new BillSaleLineOutcome(
                        l.GetProperty("id").GetInt64(),
                        l.GetProperty("catalogItemId").GetString()!,
                        l.GetProperty("kind").GetString()!,
                        l.GetProperty("quantity").GetInt32(),
                        l.GetProperty("unitPriceToman").GetInt64(),
                        l.GetProperty("lineTotalToman").GetInt64()))
                    .OrderBy(l => l.Id)
                    .ToList()
                : new List<BillSaleLineOutcome>();
            return new BillOutcome(
                bill.GetProperty("unroundedPayableToman").GetInt64(),
                bill.GetProperty("roundingToman").GetInt64(),
                bill.GetProperty("collectibleToman").GetInt64(),
                bill.GetProperty("timeGrossToman").GetInt64(),
                bill.GetProperty("buffetGrossToman").GetInt64(),
                bill.GetProperty("servicesGrossToman").GetInt64(),
                bill.GetProperty("levelDiscountToman").GetInt64(),
                bill.GetProperty("birthdayDiscountToman").GetInt64(),
                bill.GetProperty("writeOffToman").GetInt64(),
                bill.GetProperty("frozenAtUtc").GetDateTime(),
                bill.TryGetProperty("stopReason", out var sr) && sr.ValueKind != JsonValueKind.Null
                    ? sr.GetString()
                    : null,
                segments,
                saleLines);
        }
    }

    private readonly record struct BillSegmentOutcome(
        string SegmentKey,
        int PlayerCount,
        string Mode,
        long HourlyToman,
        DateTime StartUtc,
        DateTime EndUtc,
        string DisplayLine);

    private static string BillFingerprint(BillOutcome bill) => JsonSerializer.Serialize(bill);

    private readonly record struct BillSaleLineOutcome(
        long Id,
        string CatalogItemId,
        string Kind,
        int Quantity,
        long UnitPriceToman,
        long LineTotalToman);
}