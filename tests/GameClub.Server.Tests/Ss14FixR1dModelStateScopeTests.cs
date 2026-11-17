using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1dModelStateScopeTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task FixR1d_non_game_titles_invalid_body_uses_default_validation_problem_details()
    {
        var client = factory.CreateClient();
        using var content = new StringContent("[]", Encoding.UTF8, "application/json");
        var res = await client.PostAsync("/api/operator/auth/login", content);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.NotNull(res.Content.Headers.ContentType);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType!.MediaType);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("errors", out _));
        Assert.True(json.TryGetProperty("title", out _));
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.Equal(400, status.GetInt32());
    }
}