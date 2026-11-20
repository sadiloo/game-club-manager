using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace GameClub.Server.Tests;

/// <summary>
/// Documents server behaviour when invalid birthday policy is already stored (no mapper clamp).
/// </summary>
[Collection("server")]
public sealed class InpageFormsStoredBirthdayBillTests(GameClubWebApplicationFactory factory, ITestOutputHelper output)
{
    private static async Task SeedBadPolicyAsync(GameClubWebApplicationFactory factory, int percent)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.BranchMemberSettings.SingleAsync();
        settings.BirthdayPercent = percent;
        settings.BirthdayWindowKind = "zzz";
        settings.BirthdayWindowAnchor = "zzz";
        settings.BirthdayWindowDays = 0;
        await db.SaveChangesAsync();
    }

    private static async Task<(Guid SessionId, Guid CustomerId)> StartConsoleWithBirthdayMemberAsync(GameClubWebApplicationFactory factory)
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.RegisterConsole(api, mgr, "PS5-BDAY", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-BDAY", 1);
        var created = await mgr.PostAsJsonAsync("/api/operator/customers", new { name = "BDAY", mobile = "09129990001" });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await mgr.PutAsJsonAsync($"/api/operator/members/customers/{customerId}/birthday", new { year = 1380, month = 8, day = 29 });
        await mgr.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/opening-balance", new { realToman = 100_000L });
        var op = await factory.CreateOperatorClientAsync();
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        return (sessionId, customerId);
    }

    private static string FormatBillDiag(HttpResponseMessage res)
    {
        var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string code = "";
        string message = "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("code", out var c)) code = c.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("collectibleToman", out var col))
            {
                return $"HTTP {(int)res.StatusCode} collectible={col.GetInt64()} code={code} message={message}";
            }
        }
        catch
        {
            return $"HTTP {(int)res.StatusCode} body={text[..Math.Min(400, text.Length)]}";
        }

        return $"HTTP {(int)res.StatusCode} code={code} message={message} body={text[..Math.Min(200, text.Length)]}";
    }

    [Fact]
    public async Task stored_birthday_percent_150_bill_open_is_documented_without_mapper_clamp()
    {
        var (sessionId, _) = await StartConsoleWithBirthdayMemberAsync(factory);
        await SeedBadPolicyAsync(factory, 150);
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var diag = FormatBillDiag(res);
        output.WriteLine($"D6-report percent=150 invalid windowKind/anchor: {diag}");
        Assert.True(
            res.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"D6-report percent=150: {diag}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, res.StatusCode);
        if (res.StatusCode == HttpStatusCode.BadRequest)
        {
            var body = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(body.TryGetProperty("code", out _));
        }
    }

    [Fact]
    public async Task stored_birthday_percent_neg5_bill_open_is_documented_without_mapper_clamp()
    {
        var (sessionId, _) = await StartConsoleWithBirthdayMemberAsync(factory);
        await SeedBadPolicyAsync(factory, -5);
        var op = await factory.CreateOperatorClientAsync();
        var res = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var diag = FormatBillDiag(res);
        output.WriteLine($"D6-report percent=-5 invalid windowKind/anchor: {diag}");
        Assert.True(
            res.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"D6-report percent=-5: {diag}");
        Assert.NotEqual(HttpStatusCode.InternalServerError, res.StatusCode);
    }
}