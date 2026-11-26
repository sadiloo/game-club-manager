using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsBirthdayPolicyTests(GameClubWebApplicationFactory factory)
{
    private const string PercentRangeMessage = "درصد تولد باید بین ۰ و ۱۰۰ باشد.";

    private static async Task<JsonElement> GetSettingsAsync(HttpClient op)
    {
        var res = await op.GetAsync("/api/operator/members/settings/birthday");
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<int> ReadPercentFromDbAsync(GameClubWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.BranchMemberSettings.AsNoTracking().SingleAsync()).BirthdayPercent;
    }

    private static async Task SeedBaselineBirthdayAsync(HttpClient mgr)
    {
        var res = await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = 25, windowKind = "birth_month", windowDays = 3, anchor = "on_and_after" });
        res.EnsureSuccessStatusCode();
    }

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

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    [InlineData(150)]
    public async Task birthday_settings_reject_out_of_range_percent_without_persisting(int badPercent)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await SeedBaselineBirthdayAsync(mgr);
        var before = await GetSettingsAsync(mgr);
        Assert.Equal(25, before.GetProperty("percent").GetInt32());

        var res = await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent = badPercent, windowKind = "birth_month", windowDays = 3, anchor = "on_and_after" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_policy", body.GetProperty("code").GetString());
        Assert.Equal(PercentRangeMessage, body.GetProperty("message").GetString());

        var after = await GetSettingsAsync(mgr);
        Assert.Equal(25, after.GetProperty("percent").GetInt32());
        Assert.Equal(25, await ReadPercentFromDbAsync(factory));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task birthday_settings_accept_boundary_percent_and_store(int percent)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await SeedBaselineBirthdayAsync(mgr);

        var res = await mgr.PutAsJsonAsync(
            "/api/operator/members/settings/birthday",
            new { percent, windowKind = "birth_month", windowDays = 3, anchor = "on_and_after" });
        res.EnsureSuccessStatusCode();

        var after = await GetSettingsAsync(mgr);
        Assert.Equal(percent, after.GetProperty("percent").GetInt32());
        Assert.Equal(percent, await ReadPercentFromDbAsync(factory));
    }
}