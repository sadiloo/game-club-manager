using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14f gate: ruling A non-cash over-payable preview==confirm, insufficient mixed wallet, ML2 exact split, under-tender mixed.</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14fGateTests(GameClubWebApplicationFactory factory)
{
    private const string NonCashOverPayablePersian = "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.";
    private const string InsufficientWalletPersian = "موجودی کیف پول کافی نیست.";

    private static async Task PutRoundingOffAsync(HttpClient manager)
    {
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
    }

    private static async Task PutRoundingUp5kAsync(HttpClient manager)
    {
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
    }

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14f", mobile = $"0918{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenBuffet15500Async(HttpClient op, Guid customerId)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        start.EnsureSuccessStatusCode();
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 15_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task AssertNonCashOverPayableBoth400SameAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object body)
    {
        var preview = await op.PostAsJsonAsync(previewPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        Assert.Equal("payment_invalid", previewJson.GetProperty("code").GetString());
        Assert.Equal(NonCashOverPayablePersian, previewJson.GetProperty("message").GetString());
        var confirm = await op.PostAsJsonAsync(confirmPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        Assert.Equal(NonCashOverPayablePersian, confirmJson.GetProperty("message").GetString());
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
    }

    private static async Task AssertInsufficientBoth400SameAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object body)
    {
        var preview = await op.PostAsJsonAsync(previewPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        Assert.Equal(InsufficientWalletPersian, previewJson.GetProperty("message").GetString());
        var confirm = await op.PostAsJsonAsync(confirmPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        Assert.Equal(InsufficientWalletPersian, confirmJson.GetProperty("message").GetString());
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("Wallet", 500L, "CardReader", 20_000L, null, 0L)]
    [InlineData("Wallet", 500L, "CardReader", 16_000L, null, 0L)]
    [InlineData("Wallet", 1_000L, "CardReader", 15_000L, null, 0L)]
    [InlineData("Wallet", 500L, "CardToCard", 16_000L, null, 0L)]
    public async Task r14f_f1_wallet_plus_non_cash_over_payable_preview_and_confirm_400_match(
        string walletMethod,
        long walletToman,
        string nonCashMethod,
        long nonCashToman,
        string? cashMethod,
        long cashToman)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        if (cashToman > 0)
        {
            (await op.PostAsJsonAsync(
                $"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = cashToman, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        }
        else if (walletToman > 0)
        {
            (await op.PostAsJsonAsync(
                $"/api/operator/members/customers/{customerId}/wallet/top-up",
                new { method = "Cash", amountToman = Math.Min(walletToman, 10_000L), commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        }

        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new List<object> { new { method = walletMethod, amountToman = walletToman }, new { method = nonCashMethod, amountToman = nonCashToman } };
        if (cashMethod is not null)
        {
            payments.Add(new { method = cashMethod, amountToman = cashToman });
        }

        var body = new { payments };
        await AssertNonCashOverPayableBoth400SameAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r14f_f1_zero_balance_wallet500_card20k_non_cash_400_both()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "CardReader", amountToman = 20_000L },
            },
        };
        await AssertNonCashOverPayableBoth400SameAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body);
    }

    [Fact]
    public async Task r14f_f2_w60k_balance10k_card1k_insufficient_both()
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
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 60_000L },
                new { method = "CardReader", amountToman = 1_000L },
            },
        };
        await AssertNonCashOverPayableBoth400SameAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body);
    }

    [Fact]
    public async Task r14f_f1_wallet500_card15k_cash1k_over_tender_settles_with_change()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 1_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "CardReader", amountToman = 15_000L },
                new { method = "Cash", amountToman = 1_000L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        preview.EnsureSuccessStatusCode();
        var bill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        Assert.Equal(15_500, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(1_000, bill.GetProperty("changeToman").GetInt64());
    }

    [Fact]
    public async Task r14f_f2_buffet_wallet_over_balance_mixed_card_insufficient_both()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 20_000L },
                new { method = "CardReader", amountToman = 5_000L },
            },
        };
        await AssertNonCashOverPayableBoth400SameAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body);
    }

    [Fact]
    public async Task r14f_f3_buffet_ml2_wallet49500_cash500_balance49700_floor0_settles()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = 0L });
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 500L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(50_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 50_000L, payments })).EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r14f_x14_session_wallet49900_cash300_balance49700_insufficient_both()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L });
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14F-X14", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14F-X14", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_900L },
                new { method = "Cash", amountToman = 300L },
            },
        };
        await AssertInsufficientBoth400SameAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            body);
    }

    [Fact]
    public async Task r14f_d11_buffet_wallet49000_cash500_preview_confirm_400_same()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_000L },
                new { method = "Cash", amountToman = 500L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
    }

    [Fact]
    public async Task r14f_wc11_buffet_wallet500_card14k_preview_confirm_400_same()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "CardReader", amountToman = 14_000L },
            },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(previewJson.GetProperty("message").GetString(), (await ReadJsonAsync(confirm)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task r14f_buffet_wallet500_card15k_cash5k_settle_preview_change_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
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
            new { method = "CardReader", amountToman = 15_000L },
            new { method = "Cash", amountToman = 5_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(5_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("changeToman").GetInt64());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments });
        confirm.EnsureSuccessStatusCode();
        Assert.Equal(5_000, (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("changeToman").GetInt64());
    }

    [Fact]
    public void r14f_ml2_payment_splitter_wallet49500_cash500_exact_is_valid()
    {
        var memberId = Guid.NewGuid().ToString();
        var split = PaymentSplitter.Split(
            50_000,
            [
                new Tender(PaymentMethod.Wallet, 49_500, memberId),
                new Tender(PaymentMethod.Cash, 500),
            ],
            [new MemberWallet(memberId, 49_700, 0)]);
        Assert.True(split.IsValid);
        Assert.Equal(50_000, split.TenderedToman);
    }
}