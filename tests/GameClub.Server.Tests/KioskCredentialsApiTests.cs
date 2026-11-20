using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Kiosk;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskCredentialsApiTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task operator_forbidden_on_get_and_put()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/operator/kiosk-credentials")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await op.PutAsJsonAsync("/api/operator/kiosk-credentials/manager", new { newPassword = "Passw0rd" })).StatusCode);
    }

    [Fact]
    public async Task manager_sets_both_roles_and_version_bumps()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var putManager = await manager.PutAsJsonAsync(
            "/api/operator/kiosk-credentials/manager",
            new { newPassword = "KioskM1" });
        putManager.EnsureSuccessStatusCode();
        var putOperator = await manager.PutAsJsonAsync(
            "/api/operator/kiosk-credentials/operator",
            new { newPassword = "KioskO2" });
        putOperator.EnsureSuccessStatusCode();
        var status = await manager.GetFromJsonAsync<JsonElement>("/api/operator/kiosk-credentials");
        Assert.True(status.GetProperty("managerSet").GetBoolean());
        Assert.True(status.GetProperty("operatorSet").GetBoolean());
        Assert.NotNull(status.GetProperty("managerUpdatedAtUtc").GetString());
        Assert.NotNull(status.GetProperty("operatorUpdatedAtUtc").GetString());
    }

    [Fact]
    public async Task short_password_and_credentials_version_on_station_config()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var shortPwd = await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/manager", new { newPassword = "ab" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPwd.StatusCode);
        await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/manager", new { newPassword = "KioskM1" });
        await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/operator", new { newPassword = "KioskO2" });
        var api = factory.CreateClientWithKey();
        await S3TestHelpers.RegisterPc(api, "PC-KC", PilotSeed.PcNormalGroupId);
        var panelOp = await factory.CreateOperatorClientAsync();
        var codeResp = await panelOp.PostAsync("/api/operator/stations/PC-KC/pairing-code", null);
        codeResp.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeResp.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var device = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        device.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await device.Content.ReadAsStringAsync());
        var station = doc.RootElement.GetProperty("stationId").GetString();
        var kiosk = factory.CreateClient();
        kiosk.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, doc.RootElement.GetProperty("deviceId").GetGuid().ToString());
        kiosk.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, doc.RootElement.GetProperty("deviceKey").GetString());
        var config = await kiosk.GetFromJsonAsync<JsonElement>($"/api/kiosk/stations/{station}/config");
        Assert.True(config.GetProperty("credentialsVersion").GetInt32() >= 2);
        Assert.False(string.IsNullOrEmpty(config.GetProperty("kioskManagerHash").GetString()));
    }

    [Fact]
    public async Task same_password_for_both_roles_rejected()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/manager", new { newPassword = "SamePw1" });
        var second = await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/operator", new { newPassword = "SamePw1" });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

}