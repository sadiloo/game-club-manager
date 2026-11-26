using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

/// <summary>R14k ruling A: counter over-tender preview/confirm parity (OV1–OV4).</summary>
[Collection("server")]
public sealed class InpageFormsPr51R14kParityTests(GameClubWebApplicationFactory factory)
{
    private const string NonCashOverPayablePersian = "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.";

    public sealed record OvRow(
        string Id,
        bool Session,
        long NetToman,
        bool RoundingUp5k,
        object[] Payments,
        bool ExpectSuccess,
        long? ExpectedCollectible,
        long? ExpectedChange,
        (string Method, long Amount)[]? ExpectedRecorded,
        string? ExpectedMessage);

    public static IEnumerable<object[]> OvParityRows()
    {
        yield return new object[]
        {
            new OvRow(
                "OV1",
                false,
                12_000L,
                true,
                new object[] { new { method = "Cash", amountToman = 20_000L } },
                true,
                15_000L,
                5_000L,
                new[] { ("Cash", 15_000L) },
                null),
        };
        yield return new object[]
        {
            new OvRow(
                "OV1",
                true,
                12_000L,
                true,
                new object[] { new { method = "Cash", amountToman = 20_000L } },
                true,
                15_000L,
                5_000L,
                new[] { ("Cash", 15_000L) },
                null),
        };
        yield return new object[]
        {
            new OvRow(
                "OV3",
                false,
                12_000L,
                true,
                new object[]
                {
                    new { method = "CardReader", amountToman = 10_000L },
                    new { method = "Cash", amountToman = 10_000L },
                },
                true,
                15_000L,
                5_000L,
                new[] { ("CardReader", 10_000L), ("Cash", 5_000L) },
                null),
        };
        yield return new object[]
        {
            new OvRow(
                "OV3",
                true,
                12_000L,
                true,
                new object[]
                {
                    new { method = "CardReader", amountToman = 10_000L },
                    new { method = "Cash", amountToman = 10_000L },
                },
                true,
                15_000L,
                5_000L,
                new[] { ("CardReader", 10_000L), ("Cash", 5_000L) },
                null),
        };
        yield return new object[]
        {
            new OvRow(
                "OV2",
                false,
                12_000L,
                true,
                new object[] { new { method = "CardReader", amountToman = 20_000L } },
                false,
                null,
                null,
                null,
                NonCashOverPayablePersian),
        };
    }

    [Theory]
    [MemberData(nameof(OvParityRows))]
    public async Task parity_r14k_over_tender_row(OvRow row)
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        if (row.RoundingUp5k)
        {
            await PutRoundingUp5kAsync(manager);
        }
        else
        {
            await PutRoundingOffAsync(manager);
        }

        var op = await factory.CreateOperatorClientAsync();
        string previewPath;
        string confirmPath;
        var payments = row.Payments;
        if (row.Session)
        {
            var api = factory.CreateClientWithKey();
            await S3TestHelpers.RegisterConsole(api, op, $"PS5-R14K-{row.Id}", 1);
            factory.Clock.SetTehran(2026, 6, 8, 14, 0);
            var sessionId = await S3TestHelpers.StartSession(api, $"PS5-R14K-{row.Id}", 1);
            await op.PostAsJsonAsync(
                $"/api/operator/sessions/{sessionId}/sale/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = row.NetToman });
            (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
            previewPath = $"/api/operator/sessions/{sessionId}/bill/preview";
            confirmPath = $"/api/operator/sessions/{sessionId}/bill/confirm";
        }
        else
        {
            var start = await op.PostAsync("/api/operator/buffet-only/start", null);
            var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
            await op.PostAsJsonAsync(
                $"/api/operator/buffet-only/{openSaleId}/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = row.NetToman });
            (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
            previewPath = $"/api/operator/buffet-only/{openSaleId}/bill/preview";
            confirmPath = $"/api/operator/buffet-only/{openSaleId}/confirm";
        }

        var previewBody = new { payments };
        var preview = await op.PostAsJsonAsync(previewPath, previewBody);
        object confirmBody = row.ExpectSuccess && row.ExpectedCollectible is long col
            ? (object)new { previewCollectibleToman = col, payments }
            : previewBody;

        if (!row.ExpectSuccess)
        {
            Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync(confirmPath, confirmBody)).StatusCode);
            var msg = (await JsonDocument.ParseAsync(await preview.Content.ReadAsStreamAsync())).RootElement.GetProperty("message").GetString();
            Assert.Equal(row.ExpectedMessage, msg);
            return;
        }

        preview.EnsureSuccessStatusCode();
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        var bill = row.Session ? previewJson.RootElement : previewJson.RootElement.GetProperty("bill");
        Assert.Equal(row.ExpectedCollectible, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(row.ExpectedChange, bill.GetProperty("changeToman").GetInt64());

        await PaymentSettlementParityAssert.AssertEndpointsParityAsync(
            op,
            previewPath,
            confirmPath,
            previewBody,
            confirmBody,
            buffetShape: !row.Session,
            factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var lines = await db.PaymentLines.AsNoTracking().OrderBy(p => p.Method).ToListAsync();
        Assert.Equal(row.ExpectedRecorded!.Length, lines.Count);
        foreach (var (method, amount) in row.ExpectedRecorded)
        {
            var parsed = method switch
            {
                "Cash" => PaymentMethod.Cash,
                "CardReader" => PaymentMethod.CardReader,
                _ => throw new ArgumentOutOfRangeException(nameof(method), method, null),
            };
            Assert.Equal(amount, lines.Single(l => l.Method == parsed).AmountToman);
        }
    }

    [Fact]
    public async Task parity_ov4_wallet_card_non_cash_over_payable_unchanged_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await PutRoundingOffAsync(manager);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = (await (await op.PostAsJsonAsync("/api/operator/customers", new { name = "ov4", mobile = "09183334444" }))
            .Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("openSaleId").GetGuid();
        await op.PutAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 15_500L });
        (await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null)).EnsureSuccessStatusCode();
        var body = new
        {
            payments = new[]
            {
                new { method = "Wallet", amountToman = 500L },
                new { method = "CardReader", amountToman = 20_000L },
            },
        };
        var previewPath = $"/api/operator/buffet-only/{openSaleId}/bill/preview";
        var confirmPath = $"/api/operator/buffet-only/{openSaleId}/confirm";
        var preview = await op.PostAsJsonAsync(previewPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        using var previewDoc = JsonDocument.Parse(await preview.Content.ReadAsStreamAsync());
        var previewMsg = previewDoc.RootElement.GetProperty("message").GetString();
        Assert.Equal("payment_invalid", previewDoc.RootElement.GetProperty("code").GetString());
        var confirm = await op.PostAsJsonAsync(confirmPath, body);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        Assert.Equal(previewMsg, (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("message").GetString());
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
}