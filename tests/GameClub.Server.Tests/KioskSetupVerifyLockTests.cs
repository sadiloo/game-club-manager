using System.Net;
using System.Net.Http.Json;
using GameClub.Server.Kiosk;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskSetupVerifyLockTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task setup_hello_returns_checkpoint_product_and_server_id()
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = factory.CreateClient();
        var hello = await client.GetFromJsonAsync<HelloDto>("/api/kiosk/setup/hello");
        Assert.NotNull(hello);
        Assert.Equal("Checkpoint", hello!.product);
        Assert.False(string.IsNullOrWhiteSpace(hello.serverId));
    }

    [Fact]
    public async Task verify_locks_per_ip_after_two_wrong_passwords()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/manager", new { newPassword = "KioskM1" });
        await manager.PutAsJsonAsync("/api/operator/kiosk-credentials/operator", new { newPassword = "KioskO2" });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.50");
        await client.PostAsJsonAsync("/api/kiosk/setup/verify-admin-password", new { password = "wrong1" });
        await client.PostAsJsonAsync("/api/kiosk/setup/verify-admin-password", new { password = "wrong2" });
        var locked = await client.PostAsJsonAsync("/api/kiosk/setup/verify-admin-password", new { password = "KioskM1" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<LockedDto>();
        Assert.Equal("kiosk_locked", body!.code);
        Assert.True(body.retryAfterSeconds > 0);
    }

    private sealed record HelloDto(string product, string serverId);

    private sealed record LockedDto(string code, int retryAfterSeconds);
}