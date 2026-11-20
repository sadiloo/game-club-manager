using System.Net;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsStoredBirthdayBillTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task bill_open_succeeds_when_stored_birthday_policy_percent_is_out_of_range()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.RegisterConsole(api, op, "PS5-BDAY", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-BDAY", 1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await db.BranchMemberSettings.SingleAsync();
            settings.BirthdayPercent = 150;
            settings.BirthdayWindowKind = "zzz";
            settings.BirthdayWindowAnchor = "zzz";
            settings.BirthdayWindowDays = 0;
            await db.SaveChangesAsync();
        }

        var res = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        Assert.NotEqual(HttpStatusCode.InternalServerError, res.StatusCode);
        Assert.True(res.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest);
    }
}