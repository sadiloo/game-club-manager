using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class B2BurntAfterGraceServerTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task b2_example_1_operator_bill_confirm_invoice_audit_wallet()
    {
        var sessionId = await SeedExample1SessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(1_833, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(833, bill.GetProperty("writeOffToman").GetInt64());
        Assert.Equal(833, bill.GetProperty("overshootAlert").GetProperty("amountToman").GetInt64());

        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_833L } },
        });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(1_833, (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("collectibleToman").GetInt64());

        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_833L } },
        });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await DbAsync(db => db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync());
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(833, inv.GetProperty("writeOffToman").GetInt64());
        Assert.Equal(1_833, inv.GetProperty("collectibleToman").GetInt64());
        var customerId = await DbAsync(db => db.Sessions.Where(s => s.Id == sessionId).Select(s => s.CustomerId).SingleAsync());
        var wallet = await DbAsync(db => db.CustomerMembers.Where(m => m.CustomerId == customerId).Select(m => m.RealToman).SingleAsync());
        Assert.Equal(0, wallet);

        var audits = await DbAsync(db => db.AuditLog.Where(a => a.Action == "pc_overshoot" && a.DetailsJson.Contains(sessionId.ToString())).CountAsync());
        Assert.Equal(1, audits);

        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        audits = await DbAsync(db => db.AuditLog.Where(a => a.Action == "pc_overshoot" && a.DetailsJson.Contains(sessionId.ToString())).CountAsync());
        Assert.Equal(1, audits);
    }

    [Fact]
    public async Task b2_daily_and_shift_reports_list_write_offs()
    {
        var sessionId = await SeedExample1SessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_833L } },
        });

        var invoice = await DbAsync(db => db.Invoices.Include(i => i.Session).SingleAsync(i => i.SessionId == sessionId));
        var playBefore = invoice.TimeGrossToman;

        var daily = await op.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(833, daily.GetProperty("writeOffTotalToman").GetInt64());
        Assert.Equal(833, daily.GetProperty("writeOffs")[0].GetProperty("writeOffToman").GetInt64());
        Assert.Equal(invoice.Number, daily.GetProperty("writeOffs")[0].GetProperty("invoiceNumber").GetInt64());
        Assert.Equal(playBefore, daily.GetProperty("playToman").GetInt64());

        var from = invoice.IssuedAtUtc.AddHours(-1);
        var to = invoice.IssuedAtUtc.AddHours(1);
        var usage = await op.GetFromJsonAsync<JsonElement>($"/api/operator/reports/usage?fromUtc={from:o}&toUtc={to:o}");
        Assert.Equal(833, usage.GetProperty("writeOffTotalToman").GetInt64());
        Assert.Single(usage.GetProperty("writeOffs").EnumerateArray());

        var preview = await op.GetFromJsonAsync<JsonElement>("/api/operator/shift/close/preview");
        Assert.Equal(833, preview.GetProperty("writeOffTotalToman").GetInt64());
        Assert.Equal(833, preview.GetProperty("writeOffs")[0].GetProperty("writeOffToman").GetInt64());
    }

    private async Task<Guid> SeedExample1SessionAsync()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-B2", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """UPDATE rate_lines SET "HourlyToman" = 80000 WHERE "GroupId" = 'pc-normal' AND "PlayerCount" = 1""");
            var customer = new Data.Entities.CustomerEntity { Id = Guid.NewGuid(), Name = "B2Ex1", Mobile = "09128001833", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new Data.Entities.CustomerMemberEntity { CustomerId = customer.Id, RealToman = 1_833 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "b2ex1", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-B2");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "b2ex1", password = "pass", stationId = "PC-B2" }).ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await DbAsync(db => db.Sessions.Select(s => s.Id).SingleAsync());
        factory.Clock.SetTehran(2026, 6, 3, 14, 3);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 180_000, default);
        }

        return sessionId;
    }

    private async Task<T> DbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private async Task<(Guid DeviceId, string DeviceKey, string PublicKey)> PairDeviceAsync(string stationId)
    {
        var op = await factory.CreateOperatorClientAsync();
        var codeBody = await op.PostAsync($"/api/operator/stations/{stationId}/pairing-code", null);
        codeBody.EnsureSuccessStatusCode();
        var code = JsonDocument.Parse(await codeBody.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();
        var pair = await factory.CreateClient().PostAsJsonAsync("/api/kiosk/pair", new { code });
        pair.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        return (
            doc.RootElement.GetProperty("deviceId").GetGuid(),
            doc.RootElement.GetProperty("deviceKey").GetString()!,
            doc.RootElement.GetProperty("leaseVerifyPublicKeyPem").GetString()!);
    }
}
