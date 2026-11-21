using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Kiosk.Core.Admin;
using GameClub.Server.Data;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class ShiftCafeSettingsForbiddenTests(GameClubWebApplicationFactory factory)
{
    private const string MainShiftForbiddenBody =
        """{"error":"forbidden","code":"forbidden","message":"Only a manager may change cafe settings."}""";

    [Fact]
    public async Task operator_shift_settings_forbidden_body_matches_main()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var response = await op.PutAsJsonAsync("/api/operator/shift/settings", new { shiftsEnabled = true });
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(MainShiftForbiddenBody, body);
    }

    [Fact]
    public async Task operator_kiosk_free_idle_forbidden_returns_stable_code_and_persian_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var response = await op.PutAsJsonAsync(
            "/api/operator/shift/settings/kiosk-free-idle",
            new { kioskFreeUseIdleMinutes = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(KioskFreeIdleSettingCodes.Forbidden, json.GetProperty("code").GetString());
        Assert.Equal(KioskOpStrings.MgrFreeIdleErrForbidden, json.GetProperty("message").GetString());
    }

    [Fact]
    public async Task manager_kiosk_free_idle_invalid_leaves_stored_value_unchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync(
            "/api/operator/shift/settings/kiosk-free-idle",
            new { kioskFreeUseIdleMinutes = 7 });
        var bad = await manager.PutAsJsonAsync(
            "/api/operator/shift/settings/kiosk-free-idle",
            new { kioskFreeUseIdleMinutes = 241 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var json = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(KioskFreeIdleSettingCodes.Invalid, json.GetProperty("code").GetString());
        Assert.Equal(KioskOpStrings.MgrFreeIdleErrInvalid, json.GetProperty("message").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var minutes = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .BranchCafeSettings.AsNoTracking()
            .Select(s => s.KioskFreeUseIdleMinutes)
            .SingleAsync();
        Assert.Equal(7, minutes);
    }
}