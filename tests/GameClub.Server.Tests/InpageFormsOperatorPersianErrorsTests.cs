using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsOperatorPersianErrorsTests(GameClubWebApplicationFactory factory)
{
    private static readonly Regex MostlyAsciiEnglish = new(@"^[A-Za-z0-9 .,'"":\-\(\)/\[\]{}]*$", RegexOptions.Compiled);

    private static void AssertUserVisiblePersian(JsonElement body)
    {
        Assert.True(body.TryGetProperty("message", out var msgEl), "response must include message");
        var msg = msgEl.GetString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(msg));
        var raw = body.ToString();
        Assert.DoesNotContain("$.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("System.", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonException", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON", raw, StringComparison.OrdinalIgnoreCase);
        if (MostlyAsciiEnglish.IsMatch(msg))
        {
            Assert.Fail($"operator message must not be ASCII-only English: \"{msg}\"");
        }
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task sample_errors_use_persian_messages_not_english_problem_details()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");

        var badMethod = await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{Guid.NewGuid()}/wallet/top-up",
            new { method = "Wallet", amountToman = 100L, giftToman = 0L, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, badMethod.StatusCode);
        AssertUserVisiblePersian(await BodyAsync(badMethod));

        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "X", mobile = "09123334444" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var badBody = await op.PostAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new StringContent("{\"method\":\"Cash\",\"amountToman\":\"50\",\"giftToman\":0,\"commandId\":\"" + Guid.NewGuid() + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badBody.StatusCode);
        AssertUserVisiblePersian(await BodyAsync(badBody));

        var badBirthday = await mgr.PutAsync(
            "/api/operator/members/settings/birthday",
            new StringContent("{\"percent\":150,\"windowKind\":\"day_window\",\"windowDays\":3,\"anchor\":\"around\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, badBirthday.StatusCode);
        AssertUserVisiblePersian(await BodyAsync(badBirthday));

        var opForbidden = await op.PutAsync("/api/operator/members/settings/debt-floor", JsonContent.Create(new { floorToman = (long?)null }));
        Assert.Equal(HttpStatusCode.Forbidden, opForbidden.StatusCode);
        AssertUserVisiblePersian(await BodyAsync(opForbidden));

        var api = factory.CreateClientWithKey();
        var opBill = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, opBill, "PS5-PAYFA", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-PAYFA", 1);
        (await opBill.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var zeroPay = await opBill.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = 0L } } });
        Assert.Equal(HttpStatusCode.BadRequest, zeroPay.StatusCode);
        AssertUserVisiblePersian(await BodyAsync(zeroPay));
    }
}