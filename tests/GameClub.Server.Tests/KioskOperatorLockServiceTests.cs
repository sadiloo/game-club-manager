using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using Microsoft.AspNetCore.Http;
using GameClub.Kiosk.Core.Admin;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskOperatorLockServiceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task free_use_start_creates_no_invoice_rows()
    {
        await S3TestHelpers.ResetAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-FU", PilotSeed.PcNormalGroupId);
        var deviceId = Guid.NewGuid();
        db.KioskDevices.Add(new KioskDeviceEntity
        {
            Id = deviceId,
            StationId = "PC-FU",
            DeviceKeyHash = "x",
            PairedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var invoicesBefore = await db.Invoices.CountAsync();
        var id = await svc.StartFreeUseAsync("PC-FU", deviceId, "operator", default);
        var invoicesAfter = await db.Invoices.CountAsync();
        Assert.Equal(invoicesBefore, invoicesAfter);
        Assert.NotEqual(Guid.Empty, id);
        await svc.EndFreeUseAsync("PC-FU", id, "manual", default);
    }

    [Fact]
    public async Task free_use_end_replay_leaves_single_end_row()
    {
        await S3TestHelpers.ResetAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
        var reconcile = scope.ServiceProvider.GetRequiredService<KioskReconcileService>();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-REPLAY", PilotSeed.PcNormalGroupId);
        var deviceId = Guid.NewGuid();
        db.KioskDevices.Add(new KioskDeviceEntity
        {
            Id = deviceId,
            StationId = "PC-REPLAY",
            DeviceKeyHash = "x",
            PairedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        var freeUseId = await svc.StartFreeUseAsync("PC-REPLAY", deviceId, "operator", default);
        var endedAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var payload = JsonSerializer.Serialize(new { freeUseId, reason = "manual", endedAtUtc = endedAt.ToString("O") });
        var ev = new KioskOutboundEvent
        {
            EventId = Guid.NewGuid(),
            Type = "kiosk_free_use_end",
            PayloadJson = payload,
            CreatedUtc = endedAt,
        };
        await reconcile.ApplyEventsAsync(deviceId, [ev], default);
        var ev2 = new KioskOutboundEvent
        {
            EventId = Guid.NewGuid(),
            Type = ev.Type,
            PayloadJson = ev.PayloadJson,
            CreatedUtc = ev.CreatedUtc,
        };
        await reconcile.ApplyEventsAsync(deviceId, [ev2], default);
        var row = await db.KioskFreeUses.AsNoTracking().SingleAsync(r => r.Id == freeUseId);
        Assert.NotNull(row.EndedAtUtc);
        Assert.Equal("manual", row.EndReason);
        Assert.Equal(endedAt, row.EndedAtUtc);
    }

    [Fact]
    public void idle_minutes_rejects_above_240()
    {
        var ex = Assert.Throws<ApiException>(() => KioskOperatorLockService.ValidateIdleMinutes(241));
        Assert.Equal(StatusCodes.Status400BadRequest, ex.Status);
        Assert.Equal(KioskFreeIdleSettingCodes.Invalid, ex.Code);
        Assert.Equal(KioskOpStrings.MgrFreeIdleErrInvalid, ex.Message);
    }

    [Fact]
    public async Task server_restart_leaves_open_free_use_unchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        Guid freeUseId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterPc(api, "PC-UP", PilotSeed.PcNormalGroupId);
            var deviceId = Guid.NewGuid();
            db.KioskDevices.Add(new KioskDeviceEntity
            {
                Id = deviceId,
                StationId = "PC-UP",
                DeviceKeyHash = "x",
                PairedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            freeUseId = await svc.StartFreeUseAsync("PC-UP", deviceId, "operator", default);
        }

        await using var scope2 = factory.Services.CreateAsyncScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db2.KioskFreeUses.AsNoTracking().SingleAsync(r => r.Id == freeUseId);
        Assert.Null(row.EndedAtUtc);
    }

    [Fact]
    public async Task local_open_sessions_includes_open_free_uses()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-LOC", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
        var deviceId = Guid.NewGuid();
        db.KioskDevices.Add(new KioskDeviceEntity
        {
            Id = deviceId,
            StationId = "PC-LOC",
            DeviceKeyHash = "x",
            PairedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        await svc.StartFreeUseAsync("PC-LOC", deviceId, "operator", default);

        var local = await api.GetFromJsonAsync<JsonElement>("/api/local/open-sessions");
        Assert.True(local.GetProperty("openFreeUses").GetInt32() >= 1);
    }

    [Fact]
    public async Task open_free_use_goes_no_contact_after_three_minutes_without_contact()
    {
        await S3TestHelpers.ResetAsync(factory);
        var baseTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        factory.Clock.SetUtc(baseTime);

        const string stationId = "PC-STALE";
        Guid deviceId;
        Guid freeUseId;
        var lastContact = baseTime.UtcDateTime.AddMinutes(2);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
            deviceId = Guid.NewGuid();
            db.KioskDevices.Add(new KioskDeviceEntity
            {
                Id = deviceId,
                StationId = stationId,
                DeviceKeyHash = "x",
                PairedAtUtc = baseTime.UtcDateTime,
                LastSeenUtc = lastContact,
            });
            await db.SaveChangesAsync();
            freeUseId = await svc.StartFreeUseAsync(stationId, deviceId, "operator", default);
        }

        factory.Clock.SetUtc(baseTime.AddMinutes(6));

        var op = await factory.CreateOperatorClientAsync();
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = grid.EnumerateArray().Single(e => e.GetProperty("id").GetString() == stationId);
        var freeUse = tile.GetProperty("freeUse");
        Assert.True(freeUse.GetProperty("noContact").GetBoolean());
        Assert.Equal(lastContact, freeUse.GetProperty("lastContactUtc").GetDateTime());

        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var fromUtc = baseTime.UtcDateTime.AddDays(-1).ToString("O");
        var toUtc = baseTime.UtcDateTime.AddDays(1).ToString("O");
        var report = await manager.GetFromJsonAsync<JsonElement>(
            $"/api/operator/reports/free-use?fromUtc={Uri.EscapeDataString(fromUtc)}&toUtc={Uri.EscapeDataString(toUtc)}");
        var row = report.GetProperty("rows").EnumerateArray()
            .Single(e => e.GetProperty("stationId").GetString() == stationId);
        Assert.True(row.GetProperty("noContact").GetBoolean());
        Assert.Equal(120, row.GetProperty("durationSeconds").GetInt64());

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var endedAt = await db.KioskFreeUses.AsNoTracking()
                .Where(r => r.Id == freeUseId)
                .Select(r => r.EndedAtUtc)
                .SingleAsync();
            Assert.Null(endedAt);
        }
    }

    [Fact]
    public async Task kiosk_restart_end_replaces_no_contact_open_row()
    {
        await S3TestHelpers.ResetAsync(factory);
        var baseTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        factory.Clock.SetUtc(baseTime);

        const string stationId = "PC-RESTART";
        Guid deviceId;
        Guid freeUseId;
        var lastAlive = baseTime.UtcDateTime.AddMinutes(2);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
            deviceId = Guid.NewGuid();
            db.KioskDevices.Add(new KioskDeviceEntity
            {
                Id = deviceId,
                StationId = stationId,
                DeviceKeyHash = "x",
                PairedAtUtc = baseTime.UtcDateTime,
                LastSeenUtc = lastAlive,
            });
            await db.SaveChangesAsync();
            freeUseId = await svc.StartFreeUseAsync(stationId, deviceId, "operator", default);
        }

        factory.Clock.SetUtc(baseTime.AddMinutes(10));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<KioskOperatorLockService>();
            await svc.EndFreeUseAsync(stationId, freeUseId, "restart", lastAlive, default);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.KioskFreeUses.AsNoTracking().SingleAsync(r => r.Id == freeUseId);
            Assert.Equal("restart", row.EndReason);
            Assert.Equal(lastAlive, row.EndedAtUtc);
        }

        var op = await factory.CreateOperatorClientAsync();
        var grid = await op.GetFromJsonAsync<JsonElement>("/api/operator/grid");
        var tile = grid.EnumerateArray().FirstOrDefault(e => e.GetProperty("id").GetString() == stationId);
        Assert.True(tile.ValueKind == JsonValueKind.Undefined || tile.GetProperty("status").GetString() != "FreeUse");
    }

    [Fact]
    public async Task operator_lock_on_open_pc_session_stops_session_writes_audit_and_freezes_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        const string stationId = "PC-OPL";
        factory.Clock.SetTehran(2026, 7, 1, 12, 0);

        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, stationId, PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity
            {
                Id = Guid.NewGuid(),
                Name = "Lock",
                Mobile = "09121112222",
                CreatedAtUtc = factory.Clock.UtcNow.UtcDateTime,
            };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "lockme", "pass", default);
        }

        var (deviceId, deviceKey, _) = await PairDeviceAsync(stationId);
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var login = await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "lockme", password = "pass", stationId });
        login.EnsureSuccessStatusCode();
        var sessionId = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();

        factory.Clock.SetTehran(2026, 7, 1, 12, 20);
        var lockAt = factory.Clock.UtcNow.UtcDateTime;
        await using (var hbScope = factory.Services.CreateAsyncScope())
        {
            await hbScope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 1_200_000, default);
        }

        var lockResponse = await kiosk.PostAsJsonAsync(
            $"/api/kiosk/stations/{stationId}/operator-lock/session",
            new { sessionId });
        lockResponse.EnsureSuccessStatusCode();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.True(session.Status is SessionStatus.BillOpen or SessionStatus.AwaitingPayment);
            Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "kiosk.operator_lock"));
        }

        var op = await factory.CreateOperatorClientAsync();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(lockAt, bill.GetProperty("frozenAtUtc").GetDateTime());
    }

    [Fact]
    public async Task operator_lock_on_console_station_returns_400_not_pc()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-OPL", 1);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PS5-OPL");
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-OPL", 1);
        var kiosk = CreateKioskClient(deviceId, deviceKey);
        var response = await kiosk.PostAsJsonAsync(
            "/api/kiosk/stations/PS5-OPL/operator-lock/session",
            new { sessionId });
        Assert.Equal(StatusCodes.Status400BadRequest, (int)response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("not_pc", body.GetProperty("code").GetString());
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