using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14g ruling بن: cash-only underpay preview/confirm both 400, same code/message (rounded payable).</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14gCashUnderpayParityTests(GameClubWebApplicationFactory factory)
{
    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task AssertCashUnderpayParityAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object body,
        string expectedPayableDigits)
    {
        var preview = await op.PostAsJsonAsync(previewPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        Assert.Equal("payment_invalid", previewJson.GetProperty("code").GetString());
        Assert.Contains(expectedPayableDigits, previewJson.GetProperty("message").GetString()!);
        Assert.True(previewJson.TryGetProperty("collectibleToman", out _));
        Assert.True(previewJson.TryGetProperty("remainingToman", out _));

        var confirm = await op.PostAsJsonAsync(confirmPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        Assert.Equal("payment_invalid", confirmJson.GetProperty("code").GetString());
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
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
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14g-cash", mobile = $"0916{Random.Shared.Next(1000000, 9999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Theory]
    [InlineData(3_000L, "۵٬۰۰۰")]
    [InlineData(4_999L, "۵٬۰۰۰")]
    public async Task r14g_c02_w12_buffet_cash_only_under_rounded_payable(long netToman, string payableDigits)
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = netToman });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        var body = new { payments = new[] { new { method = "Cash", amountToman = netToman } } };
        await AssertCashUnderpayParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            payableDigits);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
    }

    [Fact]
    public async Task r14g_wo14_aon_writeoff_cash_under_rounded_payable()
    {
        await new KioskFixR7AcceptanceTests(factory).r14b_aon_writeoff_cash_15500_preview_confirm_both_400();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task r14g_d12_session_buffet_cash_short_of_collectible(bool session)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingUp5kAsync(manager);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var payments = new[] { new { method = "Cash", amountToman = 48_000L } };
        var body = new { payments };
        if (session)
        {
            await S3TestHelpers.RegisterConsole(api, op, "PS5-R14G-I8", 1);
            factory.Clock.SetTehran(2026, 6, 8, 14, 0);
            var sessionId = await S3TestHelpers.StartSession(api, "PS5-R14G-I8", 1);
            await op.PostAsJsonAsync(
                $"/api/operator/sessions/{sessionId}/sale/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
            (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
            await AssertCashUnderpayParityAsync(
                op,
                $"/api/operator/sessions/{sessionId}/bill/preview",
                $"/api/operator/sessions/{sessionId}/bill/confirm",
                body,
                "۵۰٬۰۰۰");
            return;
        }

        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        await AssertCashUnderpayParityAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            body,
            "۵۰٬۰۰۰");
    }
}