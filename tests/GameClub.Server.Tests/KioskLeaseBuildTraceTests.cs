using System.Net.Http.Json;
using System.Text.RegularExpressions;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

public sealed class KioskLeaseBuildTraceTests
{
    private static readonly Regex LeaseBuildLine = new(
        @"^LEASE_BUILD sessionId=\S+ mode=\S+ groupId=\S+ rateSource=\S+ band=\S+ authorizedUntilUtc=.+$",
        RegexOptions.CultureInvariant);

    [Fact]
    public async Task default_config_writes_no_lease_build_line()
    {
        await using var factory = new GameClubWebApplicationFactory();
        var output = await CaptureLeaseBuildOutputAsync(factory);
        Assert.DoesNotContain("LEASE_BUILD", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task lease_build_trace_emits_six_fields_without_money_tokens()
    {
        await using var factory = new GameClubWebApplicationFactory { KioskLeaseBuildTrace = true };
        var output = await CaptureLeaseBuildOutputAsync(factory);
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Single(l => l.StartsWith("LEASE_BUILD", StringComparison.Ordinal));
        Assert.Matches(LeaseBuildLine, line);
        Assert.DoesNotContain("paid", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gift", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wallet", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("balance", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toman", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("moneyMinutes", line, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> CaptureLeaseBuildOutputAsync(GameClubWebApplicationFactory factory)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-LB", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "Lb", Mobile = "09127778899", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 100_000 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "lbuser", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairKioskAsync(factory, "PC-LB");
        var kiosk = CreateKioskClient(factory, deviceId, deviceKey);
        (await kiosk.PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "lbuser", password = "pass", stationId = "PC-LB" })).EnsureSuccessStatusCode();
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Sessions.Select(s => s.Id).SingleAsync();

        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            await using var hbScope = factory.Services.CreateAsyncScope();
            await hbScope.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 30_000, default);
        }
        finally
        {
            Console.SetOut(original);
        }

        return writer.ToString();
    }

    private static async Task<(Guid DeviceId, string DeviceKey, string Pem)> PairKioskAsync(
        GameClubWebApplicationFactory factory,
        string stationId)
    {
        var op = await factory.CreateOperatorClientAsync();
        var code = (await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null)).EnsureSuccessStatusCode();
        var pairing = await code.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code = pairing!.GetProperty("code").GetString() });
        pair.EnsureSuccessStatusCode();
        var doc = System.Text.Json.JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }

    private static HttpClient CreateKioskClient(GameClubWebApplicationFactory factory, Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }
}