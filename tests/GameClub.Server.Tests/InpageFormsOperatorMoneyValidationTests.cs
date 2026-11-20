using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsOperatorMoneyValidationTests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new { name = "QA", mobile = $"0912{Random.Shared.Next(1000000, 9999999)}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static void AssertPersianMoney400(JsonElement body, string expectedCode)
    {
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrWhiteSpace(msg.GetString()));
        Assert.DoesNotContain("System.", msg.GetString(), StringComparison.Ordinal);
        var raw = body.ToString();
        Assert.DoesNotContain("JsonException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("OverflowException", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task top_up_rejects_string_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var json =
            $"{{\"method\":\"Cash\",\"amountToman\":\"100\",\"giftToman\":0,\"commandId\":\"{Guid.NewGuid()}\"}}";
        var res = await op.PostAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task top_up_rejects_long_max_amount()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var res = await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = long.MaxValue, giftToman = 0L, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await ReadJsonAsync(res);
        AssertPersianMoney400(body, "invalid_amount");
    }

    [Fact]
    public async Task manager_gift_rejects_string_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(op);
        var res = await op.PostAsync(
            $"/api/operator/members/customers/{customerId}/wallet/gift",
            new StringContent("{\"giftToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task shift_open_rejects_string_opening_float()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.EnableShiftsAsync(mgr);
        var res = await mgr.PostAsync(
            "/api/operator/shift/open",
            new StringContent("{\"openingFloatToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task opening_balance_rejects_string_real_toman_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PostAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new StringContent("{\"realToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task correct_top_up_rejects_string_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var topUp = await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 1000L, giftToman = 0L, commandId = Guid.NewGuid() });
        topUp.EnsureSuccessStatusCode();
        var groupId = Guid.NewGuid();
        // correction endpoint uses topUpGroupId route; use invalid body only — expect invalid_body before not_found
        var res = await mgr.PostAsync(
            $"/api/operator/members/wallet/top-ups/{groupId}/correct",
            new StringContent("{\"newAmountToman\":\"100\",\"newGiftToman\":0,\"description\":\"x\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task operator_gift_cap_rejects_string_max_toman_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            "/api/operator/members/settings/operator-gift-cap",
            new StringContent("{\"maxToman\":\"100\",\"maxPercent\":10}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task shift_close_rejects_string_cash_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.EnableShiftsAsync(mgr);
        await S3TestHelpers.OpenShiftAsync(mgr);
        var res = await mgr.PostAsync(
            "/api/operator/shift/close",
            new StringContent(
                "{\"workingDay\":{\"year\":2026,\"month\":6,\"day\":3},\"actualCashToman\":\"1\",\"actualCardReaderToman\":0,\"sessionActions\":[]}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task prepaid_amount_rejects_string_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-M8", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-M8", 1);
        var res = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/prepaid/amount",
            new StringContent("{\"amountToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }
}