using System.Net.Http.Json;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InternalTestClockTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task internal_test_clock_available_in_test_host_only()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var response = await api.PostAsJsonAsync(
            "/api/internal/test-clock",
            new { year = 2026, month = 6, day = 3, hour = 14, minute = 0 });
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task factory_mutable_clock_pins_tehran_for_billing()
    {
        await S3TestHelpers.ResetAsync(factory);
        factory.Clock.SetTehran(2026, 6, 3, 2, 0);
        Assert.Equal(
            S3TestHelpers.T(2026, 6, 3, 2, 0).UtcDateTime,
            factory.Clock.UtcNow.UtcDateTime,
            TimeSpan.FromSeconds(1));
    }
}