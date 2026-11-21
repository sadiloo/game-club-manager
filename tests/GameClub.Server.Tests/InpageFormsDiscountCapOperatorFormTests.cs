using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Billing;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsDiscountCapOperatorFormTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task operator_put_discount_cap_returns_forbidden()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.PutAsJsonAsync("/api/operator/members/settings/discount-cap", new { maxCombinedPercent = 50 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task manager_put_discount_cap_persists_and_rejects_string_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/discount-cap", new { maxCombinedPercent = 55 })).EnsureSuccessStatusCode();
        var get = await mgr.GetFromJsonAsync<JsonElement>("/api/operator/members/settings/discount-cap");
        Assert.Equal(55, get.GetProperty("maxCombinedPercent").GetInt32());

        var bad = await mgr.PutAsync(
            "/api/operator/members/settings/discount-cap",
            new StringContent("{\"maxCombinedPercent\":\"80\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_body", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        var badRange = await mgr.PutAsJsonAsync("/api/operator/members/settings/discount-cap", new { maxCombinedPercent = 101 });
        Assert.Equal(HttpStatusCode.BadRequest, badRange.StatusCode);
        Assert.Equal("invalid_cap", (await badRange.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task discount_cap_limits_combined_level_and_birthday_on_console_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await mgr.PutAsJsonAsync("/api/operator/members/settings/discount-cap", new { maxCombinedPercent = 50 });
        await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/level-ladder",
            new[] { new { name = "Bronze", minimumLifetimeHours = 0L, discountPercent = 40 }, new { name = "Gold", minimumLifetimeHours = 100L, discountPercent = 30 } });
        await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = 30, windowKind = "ExactDay", windowDays = 0, anchor = "Start" });

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var customer = await op.PostAsJsonAsync("/api/operator/customers", new { name = "cap", mobile = "09128889999" });
        customer.EnsureSuccessStatusCode();
        var customerId = (await customer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { levelName = "Bronze" });
        await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday", new { year = 1380, month = 8, day = 29 });

        await S3TestHelpers.RegisterConsole(factory.CreateClientWithKey(), op, "PS5-CAP", 1);
        var sessionId = await S3TestHelpers.StartSession(factory.CreateClientWithKey(), "PS5-CAP", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        bill.EnsureSuccessStatusCode();
        var json = await bill.Content.ReadFromJsonAsync<JsonElement>();
        var timeGross = json.GetProperty("timeGrossToman").GetInt64();
        var levelDisc = json.GetProperty("levelDiscountToman").GetInt64();
        var birthDisc = json.GetProperty("birthdayDiscountToman").GetInt64();
        Assert.True(levelDisc + birthDisc > 0);
        Assert.True(levelDisc + birthDisc <= MoneyMath.PercentOf(timeGross, 50));
    }
}