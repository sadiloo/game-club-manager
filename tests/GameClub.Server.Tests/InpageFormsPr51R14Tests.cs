using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Kiosk;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R14Tests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient client, object body)
    {
        var res = await client.PutAsJsonAsync("/api/operator/settings/rounding", body);
        res.EnsureSuccessStatusCode();
    }

    private static object RoundingBody(
        long? stepToman = null,
        string? direction = null,
        long walletMinBillToman = 5_000L,
        bool applyToWallet = true)
        => new
        {
            enabled = true,
            stepToman = stepToman ?? 5_000L,
            direction = direction ?? "Up",
            applyToWallet,
            walletMinBillToman,
            allowWalletShortBalanceDebt = true,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> BuffetCheckoutAsync(HttpClient op, Guid customerId, long customAmountToman)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }

    [Fact]
    public void r14_q1_sum_mismatch_message_includes_rounded_payable()
    {
        var msg = OperatorPaymentErrors.Localize(
            new PaymentError(PaymentErrors.SumMismatch, ""),
            15_000);
        Assert.Contains("مبلغ قابل پرداخت", msg);
        Assert.Contains("۱۵٬۰۰۰", msg);
        Assert.Contains("تومان", msg);
    }

    [Fact]
    public async Task r14_q5_get_bill_matches_preview_after_rounding_settings_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(stepToman: 1_000L, direction: "Down", walletMinBillToman: 1_000L));
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 49_000L);
        await PutRoundingAsync(manager, RoundingBody());
        var getBill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/buffet-only/{openSaleId}");
        var billCollectible = getBill.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Cash", amountToman = 50_000L } } });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        Assert.Equal(previewCollectible, billCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = previewCollectible,
                payments = new[] { new { method = "Cash", amountToman = 50_000L } },
            });
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
    }

    [Fact]
    public async Task r14_q1_api_reject_payment_invalid_includes_payable_toman()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 15_000L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Cash", amountToman = 14_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var body = await confirm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        var message = body.GetProperty("message").GetString();
        Assert.NotNull(message);
        Assert.Contains("۱۵٬۰۰۰", message);
        Assert.Contains("مبلغ قابل پرداخت", message);
    }

    [Fact]
    public async Task r14_d2_mixed_5000_wallet_min_10k_preview_equals_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(walletMinBillToman: 10_000L));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 5_000L);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 1_000L },
            new { method = "Cash", amountToman = 4_000L },
        };
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(5_000, previewCollectible);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(5_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task r14_d2_mixed_8000_wallet_min_10k_not_above_all_cash_rounded()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(walletMinBillToman: 10_000L));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 8_000L);
        var previewCash = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Cash", amountToman = 10_000L } } });
        previewCash.EnsureSuccessStatusCode();
        var allCashCollectible = (await previewCash.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(10_000, allCashCollectible);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 2_000L },
            new { method = "Cash", amountToman = 6_000L },
        };
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments });
        preview.EnsureSuccessStatusCode();
        var mixedCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(8_000, mixedCollectible);
        Assert.True(mixedCollectible <= allCashCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = mixedCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(8_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task r14_b2_ks1_pc_member_cash_20000_preview_confirm_20000()
        => await AssertKsPreviewConfirmAsync([new { method = "Cash", amountToman = 20_000L }], expectWalletSpendable: null);

    [Fact]
    public async Task r14_b2_ks3_pc_member_wallet_20000_preview_confirm_debit_20000()
        => await AssertKsPreviewConfirmAsync([new { method = "Wallet", amountToman = 20_000L }], expectWalletSpendable: 80_000L);

    [Fact]
    public async Task r14_b2_ks5_pc_member_mixed_preview_confirm_20000()
        => await AssertKsPreviewConfirmAsync(
            [new { method = "Wallet", amountToman = 2_000L }, new { method = "Cash", amountToman = 18_000L }],
            expectWalletSpendable: null);

    [Fact]
    public async Task r14_b2_ks8_apply_wallet_off_cash_20000_preview_confirm_20000()
    {
        var (sessionId, op) = await SeedPcMemberSession17000Async(applyToWallet: false);
        var payments = new[] { new { method = "Cash", amountToman = 20_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(20_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(20_000, await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact(Skip = "B-3 HOLD WM5: ledger debit 5000 vs invoice/payment line 3000 pending owner choice (Collect fixed 5k vs WalletMinBillToman 0).")]
    public async Task r14_b3_wm5_wallet_ledger_must_match_invoice_and_payment_line_HOLD()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, RoundingBody(walletMinBillToman: 0L));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetCheckoutAsync(op, customerId, 3_000L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                previewCollectibleToman = 3_000L,
                payments = new[] { new { method = "Wallet", amountToman = 3_000L } },
            });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        var walletLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Wallet);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        var ledgerDebit = -(spend.RealDeltaToman + spend.GiftDeltaToman);
        var lineDrawn = walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman;
        Assert.Equal(invoice.CollectibleToman, lineDrawn);
        Assert.Equal(invoice.CollectibleToman, ledgerDebit);
        Assert.Equal(walletLine.AmountToman, ledgerDebit);
    }

    [Fact]
    public async Task r14_po3_session_wallet_17000_lift_payment_line_matches_ledger_debit_20000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        const string station = "ps5-r14po3b";
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 17_000L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = 20_000L,
                payments = new[] { new { method = "Wallet", amountToman = 17_000L } },
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var walletLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Wallet);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        Assert.Equal(20_000, walletLine.AmountToman);
        Assert.Equal(20_000, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        Assert.Equal(-20_000, spend.RealDeltaToman + spend.GiftDeltaToman);
        Assert.Equal(20_000, await db.Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
    }

    [Fact]
    public async Task r14_po3_session_wallet_lift_payment_line_matches_ledger_debit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await OpenSessionBuffet49500Async(api, op, customerId);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new
            {
                previewCollectibleToman = 50_000L,
                payments = new[] { new { method = "Wallet", amountToman = 49_500L } },
            })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var walletLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Wallet);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        Assert.Equal(50_000, walletLine.AmountToman);
        Assert.Equal(50_000, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        Assert.Equal(-50_000, spend.RealDeltaToman + spend.GiftDeltaToman);
    }

    private async Task AssertKsPreviewConfirmAsync(object[] payments, long? expectWalletSpendable, bool applyToWallet = true)
    {
        var (sessionId, op) = await SeedPcMemberSession17000Async(applyToWallet);
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var body = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(17_000, body.GetProperty("unroundedPayableToman").GetInt64());
        Assert.Equal(3_000, body.GetProperty("roundingToman").GetInt64());
        var previewCollectible = body.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(20_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(20_000, await db.Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
        if (expectWalletSpendable is long expected)
        {
            var customerId = await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.CustomerId).SingleAsync();
            var spendable = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
            Assert.Equal(expected, spendable.GetProperty("spendableToman").GetInt64());
        }
    }

    private async Task<Guid> OpenSessionBuffet49500Async(HttpClient api, HttpClient op, Guid customerId)
    {
        const string station = "ps5-r14po3";
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return sessionId;
    }

    private static async Task PutRoundingApplyWalletAsync(HttpClient manager, bool applyToWallet)
    {
        var res = await manager.PutAsJsonAsync(
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

    private async Task<(Guid SessionId, HttpClient Op)> SeedPcMemberSession17000Async(bool applyToWallet)
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(factory.CreateClientWithKey(), "PC-1", PilotSeed.PcNormalGroupId);
        await using (var setup = factory.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE rate_lines SET "HourlyToman" = 60000 WHERE "GroupId" = 'pc-normal' AND "PlayMode" = 'Online' AND "PlayerCount" IS NULL AND "IsSpecialBand" = false""");
            var customer = new CustomerEntity { Id = Guid.NewGuid(), Name = "KS", Mobile = $"0912{Random.Shared.Next(1000000, 9999999)}", CreatedAtUtc = DateTime.UtcNow };
            db.Customers.Add(customer);
            db.CustomerMembers.Add(new CustomerMemberEntity { CustomerId = customer.Id, RealToman = 100_000 });
            await db.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<KioskCustomerAuthService>()
                .SetCredentialAsync(customer.Id, "ksr14", "pass", default);
        }

        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingApplyWalletAsync(manager, applyToWallet);

        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var (deviceId, deviceKey, _) = await PairDeviceAsync("PC-1");
        (await CreateKioskClient(deviceId, deviceKey).PostAsJsonAsync(
            "/api/kiosk/auth/login",
            new { loginName = "ksr14", password = "pass", stationId = "PC-1" })).EnsureSuccessStatusCode();
        Guid sessionId;
        await using (var lookup = factory.Services.CreateAsyncScope())
        {
            var db = lookup.ServiceProvider.GetRequiredService<AppDbContext>();
            sessionId = await db.Sessions.Select(s => s.Id).SingleAsync();
            var session = await db.Sessions.Include(s => s.Segments).Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .FirstAsync(s => s.Id == sessionId);
            var start = factory.Clock.UtcNow.UtcDateTime;
            session.StartedAtUtc = start;
            session.LastCheckpointUtc = start.AddMinutes(2);
            session.Segments.Clear();
            session.Segments.Add(new PlaySegmentEntity
            {
                SegmentKey = "a",
                StartUtc = start,
                EndUtc = start.AddMinutes(2),
                StationId = session.StationId,
                GroupId = session.Station!.GroupId,
                Mode = PlayMode.Online,
                PlayerCount = 1,
                Kind = StationKind.Pc,
            });
            await db.SaveChangesAsync();
        }

        var op = await factory.CreateOperatorClientAsync();
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = SodaId });
        await op.PostAsync($"/api/operator/sessions/{sessionId}/force-logout", null);
        return (sessionId, op);
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