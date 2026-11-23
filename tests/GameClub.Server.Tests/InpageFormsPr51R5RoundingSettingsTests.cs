using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R5RoundingSettingsTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient client, object body)
    {
        var res = await client.PutAsJsonAsync("/api/operator/settings/rounding", body);
        res.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> GetRoundingAsync(HttpClient client)
        => (await client.GetFromJsonAsync<JsonElement>("/api/operator/settings/rounding"))!;

    private static object DefaultRoundingBody(
        bool? enabled = null,
        long? stepToman = null,
        string? direction = null,
        bool? applyToWallet = null,
        long? walletMinBillToman = null,
        bool? allowWalletShortBalanceDebt = null)
        => new
        {
            enabled = enabled ?? true,
            stepToman = stepToman ?? 5_000L,
            direction = direction ?? "Up",
            applyToWallet = applyToWallet ?? true,
            walletMinBillToman = walletMinBillToman ?? 5_000L,
            allowWalletShortBalanceDebt = allowWalletShortBalanceDebt ?? true,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r5", mobile = $"0915{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

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

    private static async Task AssertBuffetPaymentLinesAsync(
        GameClubWebApplicationFactory factory,
        long expectedWalletLineToman,
        long expectedCollectibleToman)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .SingleAsync();
        Assert.Equal(expectedCollectibleToman, invoice.CollectibleToman);
        var walletLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet);
        var cashLine = invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash);
        Assert.Equal(expectedWalletLineToman, walletLine.AmountToman);
        Assert.Equal(expectedCollectibleToman, walletLine.AmountToman + cashLine.AmountToman);
        Assert.Equal(expectedWalletLineToman, walletLine.WalletRealDrawnToman + walletLine.WalletGiftDrawnToman);
        var spend = await db.WalletLedger.AsNoTracking()
            .Where(w => w.Kind == WalletLedgerKind.InvoiceSpend)
            .SingleAsync();
        Assert.Equal(-walletLine.WalletRealDrawnToman, spend.RealDeltaToman);
    }

    private static async Task<Guid> BuffetOpenAsync(HttpClient op, Guid customerId, long customAmountToman)
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
    public async Task defaults_round_trip_operator_put_forbidden_owner_put_ok()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var owner = await factory.CreateOperatorClientAsync("owner", "5678");
        var op = await factory.CreateOperatorClientAsync();
        var cur = await GetRoundingAsync(op);
        Assert.True(cur.GetProperty("enabled").GetBoolean());
        Assert.Equal(5_000, cur.GetProperty("stepToman").GetInt64());
        Assert.True(cur.GetProperty("applyToWallet").GetBoolean());
        Assert.Equal(5_000, cur.GetProperty("walletMinBillToman").GetInt64());
        Assert.True(cur.GetProperty("allowWalletShortBalanceDebt").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PutAsJsonAsync(
            "/api/operator/settings/rounding",
            DefaultRoundingBody(enabled: false))).StatusCode);
        await PutRoundingAsync(owner, DefaultRoundingBody(stepToman: 5_000L));
        Assert.Equal(5_000, (await GetRoundingAsync(manager)).GetProperty("stepToman").GetInt64());
    }

    [Fact]
    public async Task session_full_wallet_91667_preview_equals_charge_with_defaults()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R5-91667", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R5-91667", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 91_667L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(95_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 91_667L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(95_000, invoice.CollectibleToman);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task allow_debt_floor_wallet_50000_without_method(bool allowDebt)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        await PutRoundingAsync(manager, DefaultRoundingBody(allowWalletShortBalanceDebt: allowDebt));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        if (allowDebt)
        {
            confirm.EnsureSuccessStatusCode();
            Assert.Equal(-300, await WalletSpendableAsync(op, customerId));
        }
        else
        {
            Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        }
    }

    [Fact]
    public async Task allow_debt_false_rejects_explicit_debt_method_even_with_floor()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        await PutRoundingAsync(manager, DefaultRoundingBody(allowWalletShortBalanceDebt: false));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_700L, shortRemainderMethod = "Debt" },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task short_remainder_debt_rejected_when_f11_blocks_debt()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_700L, shortRemainderMethod = "Debt" },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
    }

    [Fact]
    public async Task short_remainder_49700_cash_300_explicit_method_invoice_and_received_cash()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "CardReader", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49700,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":300}]}
            """;
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var preview = await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", content);
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new StringContent(json, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        Assert.Equal(0, await WalletSpendableAsync(op, customerId));
        using (var scope = factory.Services.CreateScope())
        {
            var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
            Assert.Equal(50_000, invoice.CollectibleToman);
        }
        (await op.PostAsJsonAsync("/api/operator/shift/close", new
        {
            workingDay = new { year = 2026, month = 6, day = 3 },
            actualCashToman = 300L,
            actualCardReaderToman = 49_700L,
        })).EnsureSuccessStatusCode();
        var income = await manager.GetFromJsonAsync<JsonElement>("/api/operator/reports/income/daily?year=2026&month=6&day=3");
        Assert.Equal(300, income.GetProperty("receivedCashToman").GetInt64());
    }

    [Fact]
    public async Task f05_scenario_s2_mixed_no_method_wallet_debit_matches_line_not_lifted()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(10_500, await WalletSpendableAsync(op, customerId));
        await AssertBuffetPaymentLinesAsync(factory, 49_500, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s9_mixed_no_method_wallet_49600_cash_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_600L },
                    new { method = "Cash", amountToman = 400L },
                },
            })).EnsureSuccessStatusCode();
        await AssertBuffetPaymentLinesAsync(factory, 49_600, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s4_mixed_no_method_wallet_49700_cash_300_spendable_zero()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_700L },
                    new { method = "Cash", amountToman = 300L },
                },
            })).EnsureSuccessStatusCode();
        Assert.Equal(0, await WalletSpendableAsync(op, customerId));
        await AssertBuffetPaymentLinesAsync(factory, 49_700, 50_000);
    }

    [Fact]
    public async Task f05_scenario_s5_short_wallet_wrong_split_no_method_rejected_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new
            {
                payments = new[]
                {
                    new { method = "Wallet", amountToman = 49_500L },
                    new { method = "Cash", amountToman = 500L },
                },
            });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task f05_scenario_s7_preview_collectible_matches_confirm_charge_attempt_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_500L },
                new { method = "Cash", amountToman = 1_000L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        preview.EnsureSuccessStatusCode();
        var previewCollectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!
            .GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(50_000, previewCollectible);
    }

    [Fact]
    public async Task explicit_cash_wrong_wallet_and_cash_amounts_rejected_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        const string json = """
            {"payments":[
            {"method":"Wallet","amountToman":49500,"shortRemainderMethod":"Cash"},
            {"method":"Cash","amountToman":500}]}
            """;
        var confirm = await op.PostAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid(await ReadJsonAsync(confirm));
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task allow_debt_false_wallet_50000_over_positive_balance_rejected_by_allocator()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, DefaultRoundingBody(allowWalletShortBalanceDebt: false));
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var err = await ReadJsonAsync(confirm);
        Assert.Equal("payment_invalid", err.GetProperty("code").GetString());
        Assert.Equal("جمع پرداختی با مبلغ قابل پرداخت برابر نیست.", err.GetProperty("message").GetString());
        Assert.Equal(before, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task allow_debt_false_wallet_49500_no_method_still_lifts_to_50000_like_defaults()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, DefaultRoundingBody(allowWalletShortBalanceDebt: false));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } })).EnsureSuccessStatusCode();
        Assert.Equal(10_000, await WalletSpendableAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task short_remainder_49700_debt_explicit_method()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var body = new
        {
            payments = new[] { new { method = "Wallet", amountToman = 49_700L, shortRemainderMethod = "Debt" } },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        (await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", body)).EnsureSuccessStatusCode();
        Assert.Equal(-300, await WalletSpendableAsync(op, customerId));
    }

    [Theory]
    [InlineData(4_999L, 4_999L)]
    [InlineData(5_000L, 5_000L)]
    [InlineData(5_001L, 10_000L)]
    public async Task buffet_wallet_preview_equals_confirm_collectible(long net, long expected)
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, net);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = expected } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(expected, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = expected } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(expected, invoice.CollectibleToman);
    }

    [Fact]
    public async Task wallet_min_bill_10000_makes_9999_wallet_exact_by_money()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(manager, DefaultRoundingBody(walletMinBillToman: 10_000L));
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 9_999L);
        var preview = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 9_999L } } });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(9_999, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 9_999L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(9_999, invoice.CollectibleToman);
        Assert.Equal(0, invoice.RoundingToman);
    }

    [Fact]
    public async Task enabled_false_reports_shift_close_cash_only_gift_not_received()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.EnableShiftsAsync(manager, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        var customerId = await CreateCustomerAsync(op);
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 50_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        (await manager.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/gift",
            new { giftToman = 50_000L })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(49_500, invoice.CollectibleToman);
        Assert.Equal(0, invoice.RoundingToman);
        var effect = await db.InvoiceMemberEffects.AsNoTracking().SingleAsync();
        Assert.True(effect.WalletGiftDrawnToman > 0);
        Assert.Equal(49_500, effect.WalletRealDrawnToman + effect.WalletGiftDrawnToman);
    }

    [Fact]
    public async Task closed_invoice_unchanged_after_each_setting_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var before = await db.Invoices.AsNoTracking().SingleAsync();

        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        await PutRoundingAsync(manager, DefaultRoundingBody(stepToman: 1_000L));
        await PutRoundingAsync(manager, DefaultRoundingBody(applyToWallet: false));
        await PutRoundingAsync(manager, DefaultRoundingBody(walletMinBillToman: 10_000L));
        await PutRoundingAsync(manager, DefaultRoundingBody(allowWalletShortBalanceDebt: false));
        await PutRoundingAsync(manager, DefaultRoundingBody());

        var after = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Equal(before.CollectibleToman, after.CollectibleToman);
        Assert.Equal(before.RoundingToman, after.RoundingToman);
        Assert.Equal(before.UnroundedPayableToman, after.UnroundedPayableToman);
    }

    [Fact]
    public async Task open_bill_read_at_payment_step_change_affects_preview()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 200_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await BuffetOpenAsync(op, customerId, 49_500L);
        var before = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 50_000L } } });
        before.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await before.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        await PutRoundingAsync(manager, DefaultRoundingBody(enabled: false));
        var after = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_500L } } });
        after.EnsureSuccessStatusCode();
        Assert.Equal(49_500, (await after.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
    }
}