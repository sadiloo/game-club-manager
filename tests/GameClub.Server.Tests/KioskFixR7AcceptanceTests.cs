using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class KioskFixR7AcceptanceTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task partial_grace_wallet1333_preview_stays_1333()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var get = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(1_333, get.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task partial_grace_wallet1333_post_preview_stays_1333()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_333L } },
        });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1_333, body.GetProperty("collectibleToman").GetInt64());
        Assert.NotEqual(6_333, body.GetProperty("collectibleToman").GetInt64());
    }

    [Fact]
    public async Task partial_grace_wallet1333_confirm_retains_500()
    {
        var sessionId = await SeedPartialGraceSessionAsync();
        var op = await factory.CreateOperatorClientAsync();
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 1_333L } },
        });
        confirm.EnsureSuccessStatusCode();
        var customerId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Sessions.Where(s => s.Id == sessionId).Select(s => s.CustomerId).SingleAsync();
        var wallet = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .CustomerMembers.Where(m => m.CustomerId == customerId).Select(m => m.RealToman).SingleAsync();
        Assert.Equal(500, wallet);
    }

    [Fact]
    public async Task settled_overshoot_plain_invoice_exposes_1500_burnt_line()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[] { new { method = "Wallet", amountToman = 500L } },
        });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(500, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_500, inv.GetProperty("writeOffToman").GetInt64());
    }

    [Fact]
    public async Task f1_writeoff_apply_wallet_off_mixed_preview_confirm_15500_wallet500_cash15000()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingAsync(manager, applyToWallet: false);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 500L },
            new { method = "Cash", amountToman = 15_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_500, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(previewCollectible, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(15_500, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_500, inv.GetProperty("writeOffToman").GetInt64());
    }

    [Fact]
    public async Task gate_mutant_f1_writeoff_apply_wallet_off_server_whole_bill_round_20000_fails()
    {
        await f1_writeoff_apply_wallet_off_mixed_preview_confirm_15500_wallet500_cash15000();
    }

    [Fact]
    public async Task r14_f1_writeoff_apply_wallet_off_cash_15500_exact_preview_equals_confirm()
    {
        var (sessionId, op) = await CreateOvershootSessionForF1Async();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[] { new { method = "Cash", amountToman = 15_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_500, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(15_500, await db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.CollectibleToman).SingleAsync());
        var cashLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(15_500, cashLine.AmountToman);
    }

    [Fact]
    public async Task r14_f1_writeoff_apply_wallet_off_cash_20000_preview_confirm_15500_cash_line_15500()
    {
        var (sessionId, op) = await CreateOvershootSessionForF1Async();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[] { new { method = "Cash", amountToman = 20_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        var previewCollectible = body!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_500, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(15_500, await db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.CollectibleToman).SingleAsync());
        var cashLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(15_500, cashLine.AmountToman);
    }

    [Fact]
    public async Task r14_f1_writeoff_apply_wallet_off_wallet_500_alone_preview_confirm_both_rejected()
    {
        var (sessionId, op) = await CreateOvershootSessionForF1Async();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[] { new { method = "Wallet", amountToman = 500L } };
        var body = new { payments };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewMsg = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString();
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 15_500L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(previewMsg, (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync(i => i.SessionId == sessionId));
    }

    [Fact]
    public async Task r14b_aon_writeoff_cash_15500_preview_confirm_both_400()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[] { new { method = "Cash", amountToman = 15_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewBody = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("payment_invalid", previewBody!.GetProperty("code").GetString());
        Assert.Contains("۲۰٬۰۰۰", previewBody.GetProperty("message").GetString());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 20_000L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmBody = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody!.GetProperty("message").GetString());
    }

    [Fact]
    public async Task r14b_aon_writeoff_cash_20000_preview_confirm_20000()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[] { new { method = "Cash", amountToman = 20_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(20_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(20_000, await db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.CollectibleToman).SingleAsync());
        Assert.Equal(20_000, await db.PaymentLines.Where(p => p.Method == PaymentMethod.Cash).Select(p => p.AmountToman).SingleAsync());
    }

    [Fact]
    public async Task r14b_aon_writeoff_mixed_under_rounded_preview_confirm_15000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 20_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        const string station = "PS5-R14B-AON12";
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 12_000L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var partialPayments = new[]
        {
            new { method = "Wallet", amountToman = 1_000L },
            new { method = "Cash", amountToman = 11_000L },
        };
        var previewPartial = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = partialPayments });
        Assert.Equal(HttpStatusCode.BadRequest, previewPartial.StatusCode);
        var partialBody = (await previewPartial.Content.ReadFromJsonAsync<JsonElement>())!;
        Assert.Contains("۱۵٬۰۰۰", partialBody.GetProperty("message").GetString());
        var confirmPartial = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = partialPayments });
        Assert.Equal(HttpStatusCode.BadRequest, confirmPartial.StatusCode);
        Assert.Equal(
            partialBody.GetProperty("message").GetString(),
            (await confirmPartial.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString());
        var fullPayments = new[]
        {
            new { method = "Wallet", amountToman = 1_000L },
            new { method = "Cash", amountToman = 14_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments = fullPayments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_000, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments = fullPayments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(15_000, await db.Invoices.Where(i => i.SessionId == sessionId).Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task f1_writeoff_apply_wallet_off_overpay_wallet500_cash20000_settles_15500_records_cash15000()
    {
        var (sessionId, op) = await CreateOvershootSessionForF1Async();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 500L },
            new { method = "Cash", amountToman = 20_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(15_500, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(15_500, invoice.CollectibleToman);
        Assert.Equal(500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(15_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
    }

    [Fact]
    public async Task settled_overshoot_buffet_invoice_exposes_1500_burnt_line()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "Cash", amountToman = 15_000L },
            },
        });
        confirm.EnsureSuccessStatusCode();
        var invoiceNumber = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.Where(i => i.SessionId == sessionId).Select(i => i.Number).SingleAsync();
        var inv = await op.GetFromJsonAsync<JsonElement>($"/api/operator/invoices/{invoiceNumber}");
        Assert.Equal(15_500, inv.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_500, inv.GetProperty("writeOffToman").GetInt64());
    }

    private async Task<Guid> SeedPartialGraceSessionAsync()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE rate_lines SET \"HourlyToman\" = 80000 WHERE \"GroupId\" = 'pc-normal' AND \"PlayMode\" = 'Online' AND \"PlayerCount\" IS NULL");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "PartialGrace", Mobile = "09128881333", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 1_833 });
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "partial1333", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync("/api/kiosk/auth/login", new { loginName = "partial1333", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        var sessionId = await factory.Services.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>().Sessions.Select(s => s.Id).SingleAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 2);
        await using (var scope2 = factory.Services.CreateAsyncScope())
        {
            await scope2.ServiceProvider.GetRequiredService<KioskCheckpointCoordinator>()
                .OnHeartbeatAsync(deviceId, sessionId, 120_000, default);
        }

        return sessionId;
    }

    private async Task<(Guid SessionId, HttpClient Op)> CreateOvershootSessionForF1Async()
    {
        var (sessionId, op) = await CreateOvershootSessionAsync(graceOff: true);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingAsync(manager, applyToWallet: false);
        return (sessionId, op);
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14b", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public async Task<(Guid SessionId, HttpClient Op)> CreateOvershootSessionAsync(bool graceOff)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db0 = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db0.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE rate_lines SET "HourlyToman" = 60000 WHERE "GroupId" = 'pc-normal' AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL AND "IsSpecialBand" = false""");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "OverR7", Mobile = "09126666333", CreatedAtUtc = DateTime.UtcNow };
            db0.Customers.Add(customer);
            db0.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 500 });
            await db0.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>().SetCredentialAsync(customer.Id, "overr7", "pass", default);
        }

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
                "/api/kiosk/auth/login",
                new { loginName = "overr7", password = "pass", stationId = "PC-1" })
            .ContinueWith(t => t.Result.EnsureSuccessStatusCode());
        if (graceOff)
        {
            await using var graceScope = factory.Services.CreateAsyncScope();
            var dbGrace = graceScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await dbGrace.BranchMemberSettings.SingleAsync();
            settings.GraceMinuteEnabled = false;
            await dbGrace.SaveChangesAsync();
        }

        Guid sessionId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<AppDbContext>();
            sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
            var session = await db.Sessions.Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstAsync(s => s.Id == sessionId);
            session.StartedAtUtc = factory.Clock.UtcNow.UtcDateTime;
            session.LastCheckpointUtc = session.StartedAtUtc.AddMinutes(2);
            session.Segments.Clear();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = session.StartedAtUtc,
                EndUtc = session.StartedAtUtc.AddMinutes(2),
                StationId = session.StationId,
                GroupId = session.Station!.GroupId,
                Mode = PlayMode.Online,
                PlayerCount = 1,
                Kind = StationKind.Pc,
            });
            await db.SaveChangesAsync();
        }

        var op = await factory.CreateOperatorClientAsync();
        return (sessionId, op);
    }

    private HttpClient CreateKioskClient(Guid deviceId, string deviceKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceIdHeader, deviceId.ToString());
        client.DefaultRequestHeaders.Add(KioskDeviceAuthMiddleware.DeviceKeyHeader, deviceKey);
        return client;
    }

    private static async Task PutRoundingAsync(HttpClient client, bool applyToWallet)
    {
        var res = await client.PutAsJsonAsync(
            "/api/operator/settings/rounding",
            new
            {
                enabled = true,
                stepToman = 5_000L,
                direction = "Up",
                applyToWallet,
                walletMinBillToman = 5_000L,
                allowWalletShortBalanceDebt = true,
            });
        res.EnsureSuccessStatusCode();
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
