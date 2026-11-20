using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task prepaid_duration_rejects_wrapped_minutes_object_with_400_and_leaves_session_unchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-F07B", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-F07B", 1);

        var before = await op.GetAsync($"/api/operator/sessions/{sessionId}/prepaid");
        before.EnsureSuccessStatusCode();
        using var beforeDoc = JsonDocument.Parse(await before.Content.ReadAsStringAsync());
        var kindBefore = beforeDoc.RootElement.GetProperty("prepaidKind").GetString();

        var res = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/prepaid/duration", new { minutes = new { value = 60 } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
        Assert.Equal(PrepaidSessionKind.None, session.PrepaidKind);

        var after = await op.GetAsync($"/api/operator/sessions/{sessionId}/prepaid");
        after.EnsureSuccessStatusCode();
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal(kindBefore, afterDoc.RootElement.GetProperty("prepaidKind").GetString());
    }
}