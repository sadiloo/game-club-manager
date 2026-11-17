using System.Net;
using System.Net.Http.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PackageEndpointsGoneTests(GameClubWebApplicationFactory factory) : IClassFixture<GameClubWebApplicationFactory>
{
    [Fact]
    public async Task legacy_package_routes_return_404()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await op.GetAsync("/api/operator/members/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await op.PutAsJsonAsync("/api/operator/members/packages/x", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await op.PostAsJsonAsync($"/api/operator/members/customers/{Guid.NewGuid()}/packages/sell", new { })).StatusCode);
    }
}