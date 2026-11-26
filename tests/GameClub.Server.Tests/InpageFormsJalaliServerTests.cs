using System.Net;
using GameClub.Billing;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsJalaliServerTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public void server_jalali_1403_12_30_exists()
    {
        var date = JalaliCalendar.ToGregorian(1403, 12, 30);
        Assert.True(date.Year >= 2024);
    }

    [Fact]
    public void server_jalali_1402_12_30_does_not_exist()
    {
        Assert.Throws<BillingException>(() => _ = new JalaliDate(1402, 12, 30));
    }

    [Fact]
    public async Task jalali_endpoint_accepts_1403_12_30()
    {
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.GetAsync("/api/operator/shift/jalali/1403/12/30");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task jalali_endpoint_rejects_1402_12_30()
    {
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.GetAsync("/api/operator/shift/jalali/1402/12/30");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}