using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsCustomerBirthdayYearTests(GameClubWebApplicationFactory factory)
{
    private static async Task<Guid> CreateCustomerAsync(HttpClient mgr)
    {
        var response = await mgr.PostAsJsonAsync("/api/operator/customers", new { name = "Birth", mobile = $"0913{Random.Shared.Next(1000000, 9999999)}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static int CurrentJalaliYear(GameClubWebApplicationFactory factory)
    {
        return JalaliDate.FromGregorian(TehranTime.Date(factory.Clock.UtcNow)).Year;
    }

    [Fact]
    public async Task customer_birthday_rejects_year_before_1300_with_400_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/birthday",
            new { year = 1299, month = 6, day = 15 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_date", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    [Fact]
    public async Task customer_birthday_rejects_future_jalali_year_with_400_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var futureYear = CurrentJalaliYear(factory) + 1;
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/birthday",
            new { year = futureYear, month = 6, day = 15 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_date", body.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(1300)]
    public async Task customer_birthday_accepts_boundary_year_1300(int year)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/birthday",
            new { year, month = 1, day = 1 });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task customer_birthday_accepts_current_jalali_year()
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var currentYear = CurrentJalaliYear(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var res = await mgr.PutAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/birthday",
            new { year = currentYear, month = 6, day = 15 });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }
}