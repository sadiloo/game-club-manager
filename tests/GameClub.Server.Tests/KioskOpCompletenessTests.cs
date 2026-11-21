using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskOpCompletenessTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task k_op_1_operator_lock_bill_matches_panel_stop_at_same_instant()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string lockStation = "PC-OPL-LOCK";
        const string stopStation = "PC-OPL-STOP";
        factory.Clock.SetTehran(2026, 7, 2, 14, 0);

        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, lockStation, PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(api, stopStation, PilotSeed.PcNormalGroupId);
        await SeedCustomerAsync("lockme", "09121112222");
        await SeedCustomerAsync("stopme", "09121113333");

        var lockSession = await StartKioskSessionAsync(lockStation, "lockme");
        var stopSession = await StartKioskSessionAsync(stopStation, "stopme");

        factory.Clock.SetTehran(2026, 7, 2, 14, 25);
        var endAt = factory.Clock.UtcNow.UtcDateTime;
        await TickHeartbeatsAsync(lockSession.DeviceId, lockSession.SessionId, 1_500_000);
        await TickHeartbeatsAsync(stopSession.DeviceId, stopSession.SessionId, 1_500_000);

        var lockResponse = await lockSession.Client.PostAsJsonAsync(
            $"/api/kiosk/stations/{lockStation}/operator-lock/session",
            new { sessionId = lockSession.SessionId });
        lockResponse.EnsureSuccessStatusCode();

        (await api.PostAsync($"/api/sessions/{stopSession.SessionId}/stop", null)).EnsureSuccessStatusCode();

        var op = await factory.CreateOperatorClientAsync();
        var lockBill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{lockSession.SessionId}/bill");
        var stopBill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{stopSession.SessionId}/bill");

        Assert.Equal(endAt, lockBill.GetProperty("frozenAtUtc").GetDateTime());
        Assert.Equal(endAt, stopBill.GetProperty("frozenAtUtc").GetDateTime());
        Assert.Equal(
            lockBill.GetProperty("collectibleToman").GetInt64(),
            stopBill.GetProperty("collectibleToman").GetInt64());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.Sessions.AsNoTracking().AnyAsync(s => s.StationId == lockStation && s.Status == SessionStatus.Open));
    }

    [Fact]
    public async Task k_op_2_free_use_creates_zero_money_rows_and_report_row_for_manual_end()
    {
        await S3TestHelpers.ResetAsync(factory);
        var baseTime = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero);
        factory.Clock.SetUtc(baseTime);

        const string stationId = "PC-FU-ZERO";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        var deviceId = Guid.NewGuid();
        db.KioskDevices.Add(new KioskDeviceEntity
        {
            Id = deviceId,
            StationId = stationId,
            DeviceKeyHash = "x",
            PairedAtUtc = baseTime.UtcDateTime,
        });
        await db.SaveChangesAsync();

        var billsBefore = await db.PendingBills.CountAsync();
        var invoicesBefore = await db.Invoices.CountAsync();
        var ledgerBefore = await db.WalletLedger.CountAsync();
        var walletBefore = await db.CustomerMembers.SumAsync(m => (long?)m.RealToman) ?? 0;

        var freeUseId = await svc.StartFreeUseAsync(stationId, deviceId, "operator", default);
        factory.Clock.SetUtc(baseTime.AddMinutes(12));
        var endAt = factory.Clock.UtcNow.UtcDateTime;
        await svc.EndFreeUseAsync(stationId, freeUseId, "manual", default);

        Assert.Equal(billsBefore, await db.PendingBills.CountAsync());
        Assert.Equal(invoicesBefore, await db.Invoices.CountAsync());
        Assert.Equal(ledgerBefore, await db.WalletLedger.CountAsync());
        Assert.Equal(walletBefore, await db.CustomerMembers.SumAsync(m => (long?)m.RealToman) ?? 0);

        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = grid.EnumerateArray().Single(e => e.GetProperty("id").GetString() == stationId);
        Assert.NotEqual("FreeUse", tile.GetProperty("status").GetString());

        var fromUtc = baseTime.UtcDateTime.AddDays(-1).ToString("O");
        var toUtc = baseTime.UtcDateTime.AddDays(1).ToString("O");
        var report = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/operator/reports/free-use?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}");
        var row = report.GetProperty("rows").EnumerateArray().Single(e => e.GetProperty("stationId").GetString() == stationId);
        Assert.Equal("manual", row.GetProperty("endReason").GetString());
        Assert.Equal(baseTime.UtcDateTime, row.GetProperty("startedAtUtc").GetDateTime());
        Assert.Equal(endAt, row.GetProperty("endedAtUtc").GetDateTime());
    }

    [Fact]
    public async Task k_op_2_manager_idle_setting_is_read_back_for_server_idle_policy()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync(
            "/api/operator/shift/settings/kiosk-free-idle",
            new { kioskFreeUseIdleMinutes = 12 });
        await using var scope = factory.Services.CreateAsyncScope();
        var minutes = await scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>().GetIdleMinutesAsync(default);
        Assert.Equal(12, minutes);
    }

    private async Task SeedCustomerAsync(string loginName, string mobile)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = loginName,
            Mobile = mobile,
            CreatedAtUtc = factory.Clock.UtcNow.UtcDateTime,
        };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
            .SetCredentialAsync(customer.Id, loginName, "pass", default);
    }

    private async Task<(Guid DeviceId, Guid SessionId, HttpClient Client)> StartKioskSessionAsync(string stationId, string loginName)
    {
        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var login = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName, password = "pass", stationId });
        login.EnsureSuccessStatusCode();
        var sessionId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        return (deviceId, sessionId, kiosk);
    }

    private async Task TickHeartbeatsAsync(Guid deviceId, Guid sessionId, long playedMs)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
            .OnHeartbeatAsync(deviceId, sessionId, playedMs, default);
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