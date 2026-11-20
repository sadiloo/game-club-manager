using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server;
using Microsoft.Extensions.Logging;
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
        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("level name", raw, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(3, root.EnumerateObject().Count());
        Assert.True(root.TryGetProperty("error", out var err));
        Assert.True(root.TryGetProperty("code", out var code));
        Assert.True(root.TryGetProperty("message", out var message));
        Assert.Equal(err.GetString(), code.GetString());
        Assert.Equal(BillingErrors.InvalidLevel, code.GetString());
        Assert.Equal("جدول سطح‌ها نامعتبر است.", message.GetString());
    }

    [Fact]
    public async Task unhandled_exception_returns_generic_500_without_details()
    {
        factory.LogCollector.Clear();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username = "manager", pin = "1234" });
        login.EnsureSuccessStatusCode();

        var res = await client.GetAsync("/api/operator/_test/unhandled");
        Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("InvalidOperationException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnostic fault", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stack", raw, StringComparison.OrdinalIgnoreCase);

        var errorLogs = factory.LogCollector.Entries
            .Where(e => e.Level == LogLevel.Error && e.Category == typeof(ApiExceptionMiddleware).FullName)
            .ToList();
        Assert.Single(errorLogs);
        Assert.Contains("GET", errorLogs[0].Message, StringComparison.Ordinal);
        Assert.Contains("/api/operator/_test/unhandled", errorLogs[0].Message, StringComparison.Ordinal);
        Assert.NotNull(errorLogs[0].Exception);
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