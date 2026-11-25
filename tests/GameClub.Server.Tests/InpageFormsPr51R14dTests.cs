using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R14dTests(GameClubWebApplicationFactory factory)
{
    private const string NonCashOverPayablePersian = "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.";

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static void AssertPaymentInvalid(JsonElement body)
    {
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    private static async Task PutRoundingOffAsync(HttpClient manager, long walletMinBillToman = 500L)
    {
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14d", mobile = $"0915{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenBuffetAsync(HttpClient op, Guid customerId, long customAmountToman)
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

    private static Task<Guid> OpenBuffet15500Async(HttpClient op, Guid customerId)
        => OpenBuffetAsync(op, customerId, 15_500L);

    [Fact]
    public async Task r14d_card20k_preview_matches_confirm_400_persian()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new[] { new { method = "CardReader", amountToman = 20_000L } };

        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewBody = await ReadJsonAsync(preview);
        AssertPaymentInvalid(previewBody);
        Assert.Equal(NonCashOverPayablePersian, previewBody.GetProperty("message").GetString());

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmBody = await ReadJsonAsync(confirm);
        AssertPaymentInvalid(confirmBody);
        Assert.Equal(NonCashOverPayablePersian, confirmBody.GetProperty("message").GetString());
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody.GetProperty("message").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
    }

    [Fact]
    public async Task r14d_wallet500_cash20k_settles_like_card_overpay_preview_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = false,
            walletMinBillToman = 0L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new object[]
        {
            new { method = "Wallet", amountToman = 500L },
            new { method = "Cash", amountToman = 20_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var bill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        Assert.Equal(15_500, bill.GetProperty("collectibleToman").GetInt64());
        var previewChange = bill.GetProperty("changeToman").GetInt64();
        Assert.Equal(5_000, previewChange);

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments });
        confirm.EnsureSuccessStatusCode();
        var confirmChange = (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("changeToman").GetInt64();
        Assert.Equal(5_000, confirmChange);
        Assert.Equal(previewChange, confirmChange);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(15_500, invoice.PaymentLines.Sum(p => p.AmountToman));
        Assert.Equal(500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(15_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
    }

    [Fact]
    public async Task r14d_a_mixed_wallet_1000_cash_3000_bill_4000_exact_preview_equals_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 4_000L);
        var payments = new object[]
        {
            new { method = "Wallet", amountToman = 1_000L },
            new { method = "Cash", amountToman = 3_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewBill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        Assert.Equal(4_000, previewBill.GetProperty("collectibleToman").GetInt64());
        var previewCollectible = previewBill.GetProperty("collectibleToman").GetInt64();
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(4_000, invoice.CollectibleToman);
        Assert.Equal(1_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(3_000, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        Assert.Equal(-1_000, spend.RealDeltaToman + spend.GiftDeltaToman);
    }

    private async Task AssertMixedThresholdPinAsync(
        long walletMinBillToman,
        long billNet,
        long walletPay,
        long cashPay,
        long expectedCollectible,
        bool expectRoundedWholeBill)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, billNet);

        var payments = new object[]
        {
            new { method = "Wallet", amountToman = walletPay },
            new { method = "Cash", amountToman = cashPay },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewBill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        var previewCollectible = previewBill.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(expectedCollectible, previewCollectible);

        var cashOnlyCollectible = InvoiceCalculator.Calculate(
            new InvoiceRequest(
                [],
                [new BuffetLine("custom", "خدمت", 1, billNet)],
                [],
                null,
                null,
                DiscountPolicy.Default,
                RoundingPolicy.Create(5_000, RoundingDirection.Up),
                new DateTimeOffset(2026, 6, 3, 15, 0, 0, TehranTime.Offset))).PayableToman;
        Assert.True(previewCollectible <= cashOnlyCollectible);

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments });
        confirm.EnsureSuccessStatusCode();
        Assert.Equal(expectedCollectible, previewCollectible);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(expectedCollectible, invoice.CollectibleToman);
        var walletLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet);
        var cashLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(walletPay, walletLine.AmountToman);
        Assert.Equal(expectedCollectible - walletPay, cashLine.AmountToman);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        var ledgerDebit = -(spend.RealDeltaToman + spend.GiftDeltaToman);
        Assert.Equal(walletLine.AmountToman, ledgerDebit);
        Assert.Equal(walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman, ledgerDebit);
        if (expectRoundedWholeBill)
        {
            Assert.True(expectedCollectible > billNet);
        }
        else
        {
            Assert.Equal(billNet, expectedCollectible);
        }
    }

    [Fact]
    public async Task r14d_b_wallet_min_5000_below_4000_mixed_exact()
        => await AssertMixedThresholdPinAsync(5_000, 4_000, 1_000, 3_000, 4_000, expectRoundedWholeBill: false);

    [Fact]
    public async Task r14d_b_wallet_min_5000_at_threshold_5000_mixed_rounded_once()
        => await AssertMixedThresholdPinAsync(5_000, 5_000, 1_000, 4_000, 5_000, expectRoundedWholeBill: false);

    [Fact]
    public async Task r14d_b_wallet_min_10000_below_9999_mixed_exact()
        => await AssertMixedThresholdPinAsync(10_000, 9_999, 5_000, 4_999, 9_999, expectRoundedWholeBill: false);

    [Fact]
    public async Task r14d_b_wallet_min_10000_at_threshold_10001_mixed_rounded_once()
        => await AssertMixedThresholdPinAsync(10_000, 10_001, 1_000, 14_000, 15_000, expectRoundedWholeBill: true);

    [Fact]
    public async Task r14d_b_wallet_min_0_below_4000_mixed_whole_bill_rounded_once()
        => await AssertMixedThresholdPinAsync(0, 4_000, 1_000, 4_000, 5_000, expectRoundedWholeBill: true);

    [Fact]
    public async Task r14d_b_wallet_min_0_at_threshold_6000_mixed_rounded_once()
        => await AssertMixedThresholdPinAsync(0, 6_000, 1_000, 9_000, 10_000, expectRoundedWholeBill: true);

    [Fact]
    public async Task gate_mutant_r14d_b_hardcoded_5k_threshold_fails_10k_below_pin()
    {
        await r14d_b_wallet_min_10000_below_9999_mixed_exact();
    }

    [Fact]
    public async Task gate_mutant_r14d_b_hardcoded_5k_threshold_fails_0_at_threshold_pin()
    {
        await r14d_b_wallet_min_0_below_4000_mixed_whole_bill_rounded_once();
    }

    [Fact]
    public async Task gate_mutant_r14d_a_rounding_below_threshold_fails_4000_exact_pin()
    {
        await r14d_a_mixed_wallet_1000_cash_3000_bill_4000_exact_preview_equals_confirm();
    }

    [Fact]
    public async Task r14d_d_wallet49500_buffet_lift_preview_equals_confirm_50000_line_ledger_invoice()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[] { new { method = "Wallet", amountToman = 49_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, previewCollectible);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = previewCollectible, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        var walletLine = await db.PaymentLines.AsNoTracking().SingleAsync(p => p.Method == PaymentMethod.Wallet);
        var spend = await db.WalletLedger.AsNoTracking().SingleAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend);
        Assert.Equal(50_000, walletLine.AmountToman);
        Assert.Equal(50_000, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        Assert.Equal(-50_000, spend.RealDeltaToman + spend.GiftDeltaToman);
    }

    [Fact]
    public async Task r14d_d_wallet49000_session_below_unrounded_preview_equals_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14D-D49K", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14D-D49K", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 27);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[] { new { method = "Wallet", amountToman = 49_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewBody = await ReadJsonAsync(preview);
        AssertPaymentInvalid(previewBody);
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmBody = await ReadJsonAsync(confirm);
        AssertPaymentInvalid(confirmBody);
        Assert.Equal(previewBody.GetProperty("message").GetString(), confirmBody.GetProperty("message").GetString());
    }

    [Fact]
    public async Task r14d_d_wallet49000_buffet_below_unrounded_rejected_preview_and_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[] { new { method = "Wallet", amountToman = 49_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task gate_mutant_r14d_d_payment_line_must_record_lifted_not_typed_49500()
    {
        await r14d_d_wallet49500_buffet_lift_preview_equals_confirm_50000_line_ledger_invoice();
    }

    [Fact]
    public async Task gate_mutant_r14d_d_ledger_debit_must_record_lifted_not_typed_49500()
    {
        await r14d_d_wallet49500_buffet_lift_preview_equals_confirm_50000_line_ledger_invoice();
    }

    [Fact]
    public async Task r14d_ml2_exact_wallet_tender_unknown_member_rejected_by_splitter()
    {
        var split = PaymentSplitter.Split(15_500, [new Tender(PaymentMethod.Wallet, 15_500, "missing-member")]);
        Assert.False(split.IsValid);
        Assert.Contains(split.Errors, error => error.Code == PaymentErrors.UnknownWallet);
        Assert.Equal(0, split.DifferenceToman);
    }

    [Fact]
    public async Task r14d_ml2_buffet_exact_wallet_line_rejects_400_before_confirm_writes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new[] { new { method = "Wallet", amountToman = 15_500L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r14d_ml2_session_exact_wallet_cash_split_rejected_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        const string station = "PS5-R14D-ML2";
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_500L },
                new { method = "Cash", amountToman = 500L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(preview));
        var confirm = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/confirm", body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task gate_mutant_r14d_ml2_splitter_check_blocks_exact_mismatch_combo()
    {
        await r14d_ml2_session_exact_wallet_cash_split_rejected_400();
    }

    [Fact]
    public async Task gate_mutant_r14d_preview_non_cash_over_payable_not_informational()
    {
        await r14d_card20k_preview_matches_confirm_400_persian();
    }
}