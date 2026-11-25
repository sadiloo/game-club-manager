using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14g: table-driven preview/confirm parity across tender suites (ruling بن).</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14gParityTests(GameClubWebApplicationFactory factory)
{
    private const string NonCashOverPayablePersian = "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.";
    private const string InsufficientWalletPersian = "موجودی کیف پول کافی نیست.";

    private sealed record ParityOutcome(
        HttpStatusCode Status,
        string? Code,
        string Message,
        long? CollectibleToman,
        long? ChangeToman,
        long? WalletDebitToman);

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static ParityOutcome ParseOutcome(HttpStatusCode status, JsonElement body, bool buffetShape)
    {
        if (status == HttpStatusCode.BadRequest)
        {
            return new ParityOutcome(
                status,
                body.GetProperty("code").GetString(),
                body.GetProperty("message").GetString()!,
                null,
                null,
                null);
        }

        if (buffetShape)
        {
            var bill = body.TryGetProperty("bill", out var nested) ? nested : body;
            return new ParityOutcome(
                status,
                null,
                string.Empty,
                bill.GetProperty("collectibleToman").GetInt64(),
                bill.TryGetProperty("changeToman", out var ch) ? ch.GetInt64() : null,
                bill.TryGetProperty("walletDebitToman", out var w) ? w.GetInt64() : null);
        }

        return new ParityOutcome(
            status,
            null,
            string.Empty,
            body.GetProperty("collectibleToman").GetInt64(),
            body.TryGetProperty("changeToman", out var change) ? change.GetInt64() : null,
            body.TryGetProperty("walletDebitToman", out var wallet) ? wallet.GetInt64() : null);
    }

    private static void AssertParity(ParityOutcome preview, ParityOutcome confirm)
    {
        Assert.Equal(preview.Status, confirm.Status);
        if (preview.Status == HttpStatusCode.BadRequest)
        {
            Assert.Equal(preview.Code, confirm.Code);
            Assert.Equal(preview.Message, confirm.Message);
            return;
        }

        Assert.Equal(preview.CollectibleToman, confirm.CollectibleToman);
        Assert.Equal(preview.ChangeToman, confirm.ChangeToman);
        Assert.Equal(preview.WalletDebitToman, confirm.WalletDebitToman);
    }

    private static async Task AssertEndpointsParityAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object previewBody,
        object? confirmBody,
        bool buffetShape)
    {
        var preview = await op.PostAsJsonAsync(previewPath, previewBody);
        var previewJson = await ReadJsonAsync(preview);
        var confirm = await op.PostAsJsonAsync(confirmPath, confirmBody ?? previewBody);
        var confirmJson = await ReadJsonAsync(confirm);
        AssertParity(
            ParseOutcome(preview.StatusCode, previewJson, buffetShape),
            ParseOutcome(confirm.StatusCode, confirmJson, buffetShape));
    }

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
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14g", mobile = $"0917{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> OpenBuffetAsync(HttpClient op, Guid customerId, long amount)
    {
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = amount });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        return openSaleId;
    }

    public static IEnumerable<object?[]> F1NonCashOverPayableRows()
    {
        yield return new object?[] { "Wallet", 500L, "CardReader", 20_000L, null, 0L };
        yield return new object?[] { "Wallet", 500L, "CardReader", 16_000L, null, 0L };
        yield return new object?[] { "Wallet", 1_000L, "CardReader", 15_000L, null, 0L };
        yield return new object?[] { "Wallet", 500L, "CardToCard", 16_000L, null, 0L };
    }

    [Theory]
    [MemberData(nameof(F1NonCashOverPayableRows))]
    public async Task parity_f1_wallet_plus_non_cash_over_payable(
        string walletMethod, long walletToman, string nonCashMethod, long nonCashToman, string? cashMethod, long cashToman)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = walletToman, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 15_500L);
        var paymentList = new List<object> { new { method = walletMethod, amountToman = walletToman }, new { method = nonCashMethod, amountToman = nonCashToman } };
        if (cashMethod is not null)
        {
            paymentList.Add(new { method = cashMethod, amountToman = cashToman });
        }

        var body = new { payments = paymentList };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            body,
            buffetShape: true);
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        Assert.Equal(NonCashOverPayablePersian, (await ReadJsonAsync(preview)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task parity_d11_mixed_under_rounded_collectible_50000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_000L },
                new { method = "Cash", amountToman = 500L },
            },
        };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            body,
            buffetShape: true);
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        var message = (await ReadJsonAsync(preview)).GetProperty("message").GetString()!;
        Assert.Contains("۵۰٬۰۰۰", message);
    }

    [Fact]
    public async Task parity_ml2_wallet49500_cash500_settles_50000()
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
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 500L },
        };
        var previewBody = new { payments };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", previewBody);
        preview.EnsureSuccessStatusCode();
        var previewJson = await ReadJsonAsync(preview);
        var collectible = previewJson.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        var confirmBody = new { previewCollectibleToman = collectible, payments };
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", confirmBody);
        confirm.EnsureSuccessStatusCode();
        AssertParity(
            ParseOutcome(preview.StatusCode, previewJson, buffetShape: true),
            ParseOutcome(confirm.StatusCode, await ReadJsonAsync(confirm), buffetShape: true));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task parity_f2_insufficient_mixed_wallet_card()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 15_500L);
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 20_000L },
                new { method = "CardReader", amountToman = 5_000L },
            },
        };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            body,
            buffetShape: true);
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", body);
        Assert.Equal(NonCashOverPayablePersian, (await ReadJsonAsync(preview)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task parity_card_only_over_payable_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffetAsync(op, customerId, 15_500L);
        var body = new { payments = new[] { new { method = "CardReader", amountToman = 20_000L } } };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            body,
            buffetShape: true);
    }

    [Fact]
    public async Task parity_session_mixed_under_rounded_same_as_buffet()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14G-D11", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14G-D11", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 49_000L },
                new { method = "Cash", amountToman = 500L },
            },
        };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            body,
            body,
            buffetShape: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task parity_o11d_debt_wallet20k_cash40k_on_50k_settles(bool session)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        await manager.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -100_000L });
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 10_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 20_000L },
            new { method = "Cash", amountToman = 40_000L },
        };
        string previewPath;
        string confirmPath;
        if (session)
        {
            await S3TestHelpers.RegisterConsole(api, op, "PS5-R14G-O11D", 1);
            factory.Clock.SetTehran(2026, 6, 8, 14, 0);
            var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14G-O11D", 1);
            await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
            await op.PostAsJsonAsync(
                $"/api/operator/sessions/{sessionId}/sale/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
            (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
            previewPath = $"/api/operator/sessions/{sessionId}/bill/preview";
            confirmPath = $"/api/operator/sessions/{sessionId}/bill/confirm";
        }
        else
        {
            var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
            previewPath = $"/api/operator/buffet-only/{openSaleId}/bill/preview";
            confirmPath = $"/api/operator/buffet-only/{openSaleId}/confirm";
        }

        var preview = await op.PostAsJsonAsync(previewPath, new { payments });
        preview.EnsureSuccessStatusCode();
        var collectible = session
            ? (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64()
            : (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, collectible);
        await AssertEndpointsParityAsync(
            op,
            previewPath,
            confirmPath,
            new { payments },
            new { previewCollectibleToman = collectible, payments },
            buffetShape: !session);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task parity_p6_s12_session_wallet45k_cash5k_debt_floor_settles()
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
            new { method = "Cash", amountToman = 30_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14G-S12", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14G-S12", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 45_000L },
            new { method = "Cash", amountToman = 5_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var collectible = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, collectible);
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments },
            new { previewCollectibleToman = collectible, payments },
            buffetShape: false);
    }

    [Fact]
    public async Task parity_a05c_session_wallet49500_cash1000_change_500()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        await S3TestHelpers.RegisterConsole(api, op, "PS5-R14G-A05C", 1);
        factory.Clock.SetTehran(2026, 6, 8, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14G-A05C", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        var previewBody = new { payments };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", previewBody);
        preview.EnsureSuccessStatusCode();
        var previewJson = await ReadJsonAsync(preview);
        Assert.Equal(50_000, previewJson.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(500, previewJson.GetProperty("changeToman").GetInt64());
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            previewBody,
            new { previewCollectibleToman = 50_000L, payments },
            buffetShape: false);
    }

    [Fact]
    public async Task parity_over_tender_wallet_cash_change_matches()
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
        var openSaleId = await OpenBuffetAsync(op, customerId, 15_500L);
        var payments = new object[]
        {
            new { method = "Wallet", amountToman = 500L },
            new { method = "CardReader", amountToman = 15_000L },
            new { method = "Cash", amountToman = 5_000L },
        };
        var previewBody = new { payments };
        await AssertEndpointsParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            previewBody,
            new { previewCollectibleToman = 15_500L, payments },
            buffetShape: true);
    }
}