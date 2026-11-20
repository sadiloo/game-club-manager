using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Server.Operator;

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
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.True(body.TryGetProperty("message", out var msg));
        Assert.False(string.IsNullOrWhiteSpace(msg.GetString()));
        Assert.DoesNotContain("System.", msg.GetString(), StringComparison.Ordinal);
        var raw = body.ToString();
        Assert.DoesNotContain("$.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JsonException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("OverflowException", raw, StringComparison.Ordinal);
    }

    private static readonly long FifteenDigits = 999_999_999_999_999L;

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

    [Fact]
    public async Task prepaid_amount_rejects_fifteen_digit_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-P16", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-P16", 1);
        var res = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/prepaid/amount",
            new { amountToman = FifteenDigits + 1 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_amount");
    }

    [Fact]
    public async Task prepaid_duration_rejects_string_minutes_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-P07", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-P07", 1);
        var res = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/prepaid/duration",
            new StringContent("{\"minutes\":\"30\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task debt_floor_rejects_string_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            "/api/operator/members/settings/debt-floor",
            new StringContent("{\"floorToman\":\"-100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task debt_floor_rejects_fifteen_digit_magnitude()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/debt-floor",
            new { floorToman = -(FifteenDigits + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_floor");
    }

    [Fact]
    public async Task operator_gift_cap_rejects_string_percent_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            "/api/operator/members/settings/operator-gift-cap",
            new StringContent("{\"maxToman\":0,\"maxPercent\":\"10\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task operator_gift_cap_rejects_max_toman_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/operator-gift-cap",
            new { maxToman = FifteenDigits + 1, maxPercent = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_cap");
    }

    [Fact]
    public async Task opening_balance_rejects_string_gift_toman_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PostAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new StringContent("{\"giftToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task opening_balance_rejects_string_debt_toman_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PostAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new StringContent("{\"debtToman\":\"100\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task opening_balance_rejects_negative_real_with_non_negative_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { realToman = -1L });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await ReadJsonAsync(res);
        AssertPersianMoney400(body, "invalid_opening");
        Assert.Equal("مبلغ نمی‌تواند منفی باشد.", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task opening_balance_rejects_gift_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { giftToman = FifteenDigits + 1 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_gift");
    }

    [Fact]
    public async Task birthday_settings_rejects_string_percent_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            "/api/operator/members/settings/birthday",
            new StringContent(
                "{\"percent\":\"10\",\"windowKind\":\"day_window\",\"windowDays\":3,\"anchor\":\"around\"}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task level_ladder_rejects_string_discount_percent_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PutAsync(
            "/api/operator/members/settings/level-ladder",
            new StringContent(
                "[{\"name\":\"A\",\"minimumLifetimeHours\":0,\"discountPercent\":\"5\"}]",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task shift_close_rejects_string_card_reader_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.EnableShiftsAsync(mgr);
        await S3TestHelpers.OpenShiftAsync(mgr);
        var res = await mgr.PostAsync(
            "/api/operator/shift/close",
            new StringContent(
                "{\"workingDay\":{\"year\":2026,\"month\":6,\"day\":3},\"actualCashToman\":0,\"actualCardReaderToman\":\"1\",\"sessionActions\":[]}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task shift_close_rejects_card_reader_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.EnableShiftsAsync(mgr);
        await S3TestHelpers.OpenShiftAsync(mgr);
        var res = await mgr.PostAsJsonAsync(
            "/api/operator/shift/close",
            new
            {
                workingDay = new { year = 2026, month = 6, day = 3 },
                actualCashToman = 0L,
                actualCardReaderToman = FifteenDigits + 1,
                sessionActions = Array.Empty<object>(),
            });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_money");
    }

    [Fact]
    public async Task bill_confirm_rejects_string_payment_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-BC", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-BC", 1);
        var res = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new StringContent(
                "{\"payments\":[{\"method\":\"Cash\",\"amountToman\":\"1\"}]}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task bill_confirm_rejects_payment_amount_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-BC2", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-BC2", 1);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var res = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = FifteenDigits + 1 } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "payment_invalid");
    }

    [Fact]
    public async Task game_title_post_rejects_non_numeric_string_hourly_toman_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PostAsync(
            "/api/operator/game-titles",
            new StringContent(
                "{\"name\":\"T\",\"enabled\":true,\"rates\":[{\"mode\":\"Offline\",\"playerCount\":1,\"hourlyToman\":\"12 000\"}]}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_rate");
    }

    [Fact]
    public async Task game_title_post_rejects_hourly_toman_overflow()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await mgr.PostAsync(
            "/api/operator/game-titles",
            new StringContent(
                $"{{\"name\":\"T\",\"enabled\":true,\"rates\":[{{\"mode\":\"Offline\",\"playerCount\":1,\"hourlyToman\":{FifteenDigits + 1}}}]}}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await ReadJsonAsync(res);
        Assert.True(
            body.GetProperty("code").GetString() is "invalid_rate" or "invalid_amount",
            body.ToString());
        AssertPersianMoney400(body, body.GetProperty("code").GetString()!);
    }

    [Fact]
    public async Task session_sale_line_rejects_string_custom_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-SL", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-SL", 1);
        var res = await op.PostAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new StringContent(
                $"{{\"catalogItemId\":\"{CatalogSeed.CustomServiceId}\",\"customAmountToman\":\"100\"}}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }

    [Fact]
    public async Task buffet_only_confirm_rejects_string_payment_amount_json()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        var res = await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new StringContent(
                "{\"payments\":[{\"method\":\"Cash\",\"amountToman\":\"1\"}]}",
                Encoding.UTF8,
                "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        AssertPersianMoney400(await ReadJsonAsync(res), "invalid_body");
    }
}