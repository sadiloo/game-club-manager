using System.Net;
using System.Net.Http.Json;
using GameClub.Billing;
namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsApiMiddlewareTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task api_middleware_maps_billing_exception_to_400()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" });
        login.EnsureSuccessStatusCode();

        var body = new[]
        {
            new { name = "", minimumLifetimeHours = 0L, discountPercent = 0 },
        };
        var res = await client.PutAsJsonAsync("/api/operator/members/settings/level-ladder", body);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(json);
        Assert.True(json.ContainsKey("code"));
        Assert.Equal(BillingErrors.InvalidLevel, json["code"]?.ToString());
    }

    [Fact]
    public async Task birthday_settings_rejects_invalid_window_days()
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" });
        login.EnsureSuccessStatusCode();

        var res = await client.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = 10, windowKind = "day_window", windowDays = 0, anchor = "on_and_after" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(json);
        Assert.Equal("invalid_window_days", json["code"]?.ToString());
    }
}