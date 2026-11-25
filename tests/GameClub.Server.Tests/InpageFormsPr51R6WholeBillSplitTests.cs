using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R6WholeBillSplitTests(GameClubWebApplicationFactory factory)
{
    private static async Task PutRoundingAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync(
            "/api/operator/settings/rounding",
            new
            {
                enabled = true,
                stepToman = 5_000L,
                direction = "Up",
                applyToWallet = true,
                walletMinBillToman = 5_000L,
                allowWalletShortBalanceDebt = true,
            })).EnsureSuccessStatusCode();
    }

    private async Task<(HttpClient op, Guid customerId, string previewUrl, string confirmUrl)> OpenBillAsync(bool session)
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(mgr);
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = -50_000L })).EnsureSuccessStatusCode();
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6sp", mobile = $"0913{Random.Shared.Next(1000000, 9999999)}" });
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 49_700L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();

        if (!session)
        {
            var start = await op.PostAsync("/api/operator/buffet-only/start", null);
            var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
            await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
            await op.PostAsJsonAsync(
                $"/api/operator/buffet-only/{openSaleId}/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
            (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
            return (
                op,
                customerId,
                $"/api/operator/buffet-only/{openSaleId}/bill/preview",
                $"/api/operator/buffet-only/{openSaleId}/confirm");
        }

        await S3TestHelpers.RegisterConsole(api, op, "ps5-r6sp", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, "ps5-r6sp", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return (
            op,
            customerId,
            $"/api/operator/sessions/{sessionId}/bill/preview",
            $"/api/operator/sessions/{sessionId}/bill/confirm");
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
    {
        var w = await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet");
        return w.GetProperty("spendableToman").GetInt64();
    }

    private static long InvoiceCollectible(JsonElement body)
        => body.TryGetProperty("collectibleToman", out var c)
            ? c.GetInt64()
            : body.GetProperty("invoice").GetProperty("collectibleToman").GetInt64();

    [Theory]
    [InlineData(false, 49_500, 500, HttpStatusCode.OK, 50_000, 200)]
    [InlineData(true, 49_500, 500, HttpStatusCode.OK, 50_000, 200)]
    [InlineData(false, 49_000, 1_000, HttpStatusCode.OK, 50_000, 700)]
    [InlineData(true, 49_000, 1_000, HttpStatusCode.OK, 50_000, 700)]
    [InlineData(false, 49_000, 5_000, HttpStatusCode.OK, 50_000, 700)]
    [InlineData(true, 49_000, 5_000, HttpStatusCode.OK, 50_000, 700)]
    public async Task pr51_r6_split_matrix_whole_bill_collectible(
        bool session,
        long walletToman,
        long cashToman,
        HttpStatusCode expectedStatus,
        long expectedCollectible,
        long expectedSpendableAfter)
    {
        var (op, customerId, pv, cf) = await OpenBillAsync(session);
        var before = await WalletSpendableAsync(op, customerId);
        Assert.Equal(49_700, before);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = walletToman },
            new { method = "Cash", amountToman = cashToman },
        };
        var preview = await op.PostAsJsonAsync(pv, new { payments });
        if (expectedStatus != HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        }
        else
        {
            preview.EnsureSuccessStatusCode();
        }
        var previewBody = (await preview.Content.ReadFromJsonAsync<JsonElement>())!;
        var previewCollectible = previewBody.TryGetProperty("collectibleToman", out var pc)
            ? pc.GetInt64()
            : previewBody.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(expectedCollectible, previewCollectible);

        var confirm = await op.PostAsJsonAsync(cf, new { previewCollectibleToman = previewCollectible, payments });
        Assert.Equal(expectedStatus, confirm.StatusCode);
        if (expectedStatus == HttpStatusCode.OK)
        {
            var body = await confirm.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedCollectible, InvoiceCollectible(body));
            using var scope = factory.Services.CreateScope();
            Assert.Equal(
                expectedCollectible,
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().Select(i => i.CollectibleToman).SingleAsync());
        }
        else
        {
            var body = await confirm.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("payment_invalid", body!.GetProperty("code").GetString());
            using var scope = factory.Services.CreateScope();
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.CountAsync());
        }

        Assert.Equal(expectedSpendableAfter, await WalletSpendableAsync(op, customerId));
    }
}