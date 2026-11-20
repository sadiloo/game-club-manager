using System.Net;
using System.Net.Http.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPrepaidDurationTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task prepaid_duration_rejects_zero_minutes_with_invalid_minutes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-F07", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-F07", 1);

        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("invalid_minutes", doc.RootElement.GetProperty("code").GetString());
    }
}