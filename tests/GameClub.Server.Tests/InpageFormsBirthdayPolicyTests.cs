using System.Net;
using System.Net.Http.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsBirthdayPolicyTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task birthday_settings_reject_invalid_window_kind_with_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await op.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = 10, windowKind = "zzz", windowDays = 3, anchor = "around" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task birthday_settings_reject_percent_over_100_with_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync("manager", "1234");
        var res = await op.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = 150, windowKind = "birth_month", windowDays = 1, anchor = "on_and_after" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}