using System.Net;
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

/// <summary>Brief §10.3 cafe scenarios (matrix gaps not covered by oracle/toggle files).</summary>
[Collection("server")]
public sealed class PcInternetMergeBarServerTests(GameClubWebApplicationFactory factory)
{
    private static async Task EnsureOfflineRatesAsync(HttpClient mgr)
    {
        (await mgr.PutAsJsonAsync(
            $"/api/operator/pc-rates/{PilotSeed.PcNormalGroupId}",
            new { onlineDay = 80_000L, onlineNight = 100_000L, offlineDay = 60_000L, offlineNight = 80_000L }))
            .EnsureSuccessStatusCode();
    }

    private async Task<uint> RowVersionAsync(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.AsNoTracking()
            .Where(s => s.Id == sessionId).Select(s => s.RowVersion).SingleAsync();
    }

    [Fact]
    public async Task PcInternetToggle_Unauthenticated_401()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-401", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-401");
        var anonymous = factory.CreateClient();
        var res = await anonymous.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task PcInternetToggle_OfflineRateMissing_NoSegment_NoVersionBump()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-NO", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-NO");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var beforeSegments = await db.Segments.CountAsync(s => s.SessionId == sessionId);
        var beforeVersion = await db.StationInternetStates.Where(s => s.StationId == "PC-NO").Select(s => s.DesiredVersion).SingleAsync();
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("offline_rate_missing", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(beforeSegments, await db.Segments.CountAsync(s => s.SessionId == sessionId));
        Assert.Equal(beforeVersion, await db.StationInternetStates.Where(s => s.StationId == "PC-NO").Select(s => s.DesiredVersion).SingleAsync());
        Assert.False(await db.AuditLog.AnyAsync(a => a.Action == "session.internet"));
    }

    [Fact]
    public async Task PcInternetToggle_SameState_NoOp()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-NOOP", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-NOOP");
        var before = await dbSegmentCount(sessionId);
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = true,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        res.EnsureSuccessStatusCode();
        Assert.Equal(before, await dbSegmentCount(sessionId));
    }

    [Fact]
    public async Task PcInternet_SessionStop_BumpsDesiredOn_AutoOnAudit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-END", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-END");
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        (await api.PostAsync($"/api/sessions/{sessionId}/stop", null)).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.StationInternetStates.SingleAsync(s => s.StationId == "PC-END");
        Assert.True(row.DesiredOn);
        Assert.Equal("session_end", row.DesiredReason);
        Assert.True(await db.AuditLog.AnyAsync(a => a.Action == "internet.auto_on"));
    }

    [Fact]
    public async Task PcInternet_KioskMemberStart_UsesOnlineMode()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-KM", PilotSeed.PcNormalGroupId);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Km", Mobile = "09123333222", CreatedAtUtc = DateTime.UtcNow };
        db.Customers.Add(customer);
        db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 50_000 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "km", "pass", default);
        var op = await factory.CreateOperatorClientAsync();
        var codeRes = await op.PostAsync("/api/operator/stations/PC-KM/pairing-code", null);
        codeRes.EnsureSuccessStatusCode();
        var code = (await codeRes.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString();
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var pairDoc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        var deviceId = pairDoc.RootElement.GetProperty("deviceId").GetGuid();
        var deviceKey = pairDoc.RootElement.GetProperty("deviceKey").GetString()!;
        var kiosk = factory.CreateClient();
        kiosk.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        kiosk.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var login = await kiosk.PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "km", password = "pass", stationId = "PC-KM" });
        login.EnsureSuccessStatusCode();
        var mode = await db.Sessions.AsNoTracking().OrderByDescending(s => s.StartedAtUtc).Select(s => s.Mode).FirstAsync();
        Assert.Equal(PlayMode.Online, mode);
    }

    [Fact]
    public async Task PcInternet_L13_SessionBeforeOfflineSave_StillRefusedAfterSave()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-L13", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PC-L13");
        await EnsureOfflineRatesAsync(mgr);
        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/internet", new
        {
            on = false,
            expectedVersion = await RowVersionAsync(sessionId),
            commandId = Guid.NewGuid(),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal("offline_rate_missing", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task PcInternet_Heartbeat_AckCarriesSignedInternet_WithoutSession()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-HB", PilotSeed.PcNormalGroupId);
        using (var seedScope = factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.StationInternetStates.Add(new StationInternetStateEntity
            {
                StationId = "PC-HB",
                Epoch = Guid.NewGuid(),
                DesiredOn = true,
                DesiredVersion = 1,
                DesiredAtUtc = factory.Clock.UtcNow.UtcDateTime,
                DesiredReason = "session_start",
            });
            db.SaveChanges();
        }

        var op = await factory.CreateOperatorClientAsync();
        var codeRes = await op.PostAsync("/api/operator/stations/PC-HB/pairing-code", null);
        codeRes.EnsureSuccessStatusCode();
        var code = (await codeRes.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString();
        var pairDoc = JsonDocument.Parse(await (await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code })).Content.ReadAsStringAsync());
        var deviceId = pairDoc.RootElement.GetProperty("deviceId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var ack = await scope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
            .OnHeartbeatAsync(deviceId, null, 0, default);
        Assert.NotNull(ack.Internet);
        Assert.NotNull(ack.Internet!.SignatureBase64);
    }

    [Fact]
    public async Task PcInternet_KioskConnected_false_after_15s_without_heartbeat()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        await EnsureOfflineRatesAsync(mgr);
        await S3TestHelpers.RegisterPc(api, "PC-DSC", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 2, 12, 0);
        await S3TestHelpers.StartSession(api, "PC-DSC");
        var codeRes = await op.PostAsync("/api/operator/stations/PC-DSC/pairing-code", null);
        codeRes.EnsureSuccessStatusCode();
        var code = (await codeRes.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("code").GetString();
        var pairDoc = JsonDocument.Parse(await (await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code })).Content.ReadAsStringAsync());
        var deviceId = pairDoc.RootElement.GetProperty("deviceId").GetGuid();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = await db.KioskDevices.SingleAsync(d => d.Id == deviceId);
        device.LastSeenUtc = factory.Clock.UtcNow.UtcDateTime.AddSeconds(-20);
        await db.SaveChangesAsync();
        var tiles = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/grid");
        var tile = tiles!.Single(t => t.GetProperty("id").GetString() == "PC-DSC");
        Assert.False(tile.GetProperty("kioskConnected").GetBoolean());
    }

    private async Task<int> dbSegmentCount(Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Segments.CountAsync(s => s.SessionId == sessionId);
    }
}