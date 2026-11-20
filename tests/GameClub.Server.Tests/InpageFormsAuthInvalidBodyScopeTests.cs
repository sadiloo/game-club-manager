using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GameClub.Server.Tests;

/// <summary>
/// f07d0c2 keeps ASP.NET default problem+json validation for <c>/api/operator/auth/*</c> only,
/// so kiosk/operator login continues to expose field-level login errors (Ss14FixR1d scope).
/// All other operator money endpoints use Persian <c>invalid_body</c> from Program.cs.
/// </summary>
[Collection("server")]
public sealed class InpageFormsAuthInvalidBodyScopeTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task operator_auth_login_still_uses_problem_json_on_invalid_body()
    {
        var client = factory.CreateClient();
        using var content = new StringContent("[]", Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/api/operator/auth/login", content);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task operator_wallet_top_up_uses_persian_invalid_body_not_problem_details()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "QA", mobile = "09128887777" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var json =
            $"{{\"method\":\"Cash\",\"amountToman\":\"100\",\"giftToman\":0,\"commandId\":\"{Guid.NewGuid()}\"}}";
        var res = await op.PostAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal("invalid_body", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("درخواست نامعتبر است.", doc.RootElement.GetProperty("message").GetString());
    }
}