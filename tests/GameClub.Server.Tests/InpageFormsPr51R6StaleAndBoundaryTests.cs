using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static GameClub.Server.Operator.CatalogSeed;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsPr51R6StaleAndBoundaryTests(GameClubWebApplicationFactory factory)
{
    const string On = """{"enabled":true,"stepToman":5000,"direction":"Up","applyToWallet":true,"walletMinBillToman":5000,"allowWalletShortBalanceDebt":true}""";
    const string Off = """{"enabled":false,"stepToman":5000,"direction":"Up","applyToWallet":true,"walletMinBillToman":5000,"allowWalletShortBalanceDebt":true}""";
    const string NoWallet = """{"enabled":true,"stepToman":5000,"direction":"Up","applyToWallet":false,"walletMinBillToman":5000,"allowWalletShortBalanceDebt":true}""";

    static async Task PutRoundingAsync(HttpClient client, string json)
    {
        (await client.PutAsync("/api/operator/settings/rounding", new StringContent(json, Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
    }

    async Task<(HttpClient op, HttpClient mgr, string pv, string cf, Guid cust)> Setup(bool session, long topUp, long? floor, string settings)
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(mgr, settings);
        if (floor is long f)
        {
            (await mgr.PutAsJsonAsync("/api/operator/members/settings/debt-floor", new { floorToman = f })).EnsureSuccessStatusCode();
        }

        var c = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6", mobile = $"0912{Random.Shared.Next(1000000, 9999999)}" });
        var cust = (await c.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (topUp > 0)
        {
            (await op.PostAsJsonAsync(
                $"/api/operator/members/customers/{cust}/wallet/top-up",
                new { method = "Cash", amountToman = topUp, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        }

        if (!session)
        {
            var s = await op.PostAsync("/api/operator/buffet-only/start", null);
            var id = (await s.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
            await op.PutAsJsonAsync($"/api/operator/buffet-only/{id}/customer", new { customerId = cust });
            await op.PostAsJsonAsync(
                $"/api/operator/buffet-only/{id}/lines",
                new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
            (await op.PostAsync($"/api/operator/buffet-only/{id}/checkout", null)).EnsureSuccessStatusCode();
            return (op, mgr, $"/api/operator/buffet-only/{id}/bill/preview", $"/api/operator/buffet-only/{id}/confirm", cust);
        }

        await S3TestHelpers.RegisterConsole(api, op, "ps5-r6", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sid = await S3TestHelpers.StartSession(api, "ps5-r6", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sid}/customer", new { customerId = cust });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sid}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        (await op.PostAsync($"/api/operator/sessions/{sid}/bill/open", null)).EnsureSuccessStatusCode();
        return (op, mgr, $"/api/operator/sessions/{sid}/bill/preview", $"/api/operator/sessions/{sid}/bill/confirm", cust);
    }

    static async Task<long> PreviewCollectible(HttpClient op, string url, string payments)
    {
        var r = await op.PostAsync(url, new StringContent($$"""{"payments":{{payments}}}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var e = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return e.TryGetProperty("collectibleToman", out var c) ? c.GetInt64() : e.GetProperty("bill").GetProperty("collectibleToman").GetInt64();
    }

    static async Task<(HttpStatusCode, string?, JsonElement)> Confirm(HttpClient op, string url, long token, string payments)
    {
        var r = await op.PostAsync(
            url,
            new StringContent($$"""{"previewCollectibleToman":{{token}},"payments":{{payments}}}""", Encoding.UTF8, "application/json"));
        var e = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
        return (r.StatusCode, e.ValueKind == JsonValueKind.Object && e.TryGetProperty("code", out var c) ? c.GetString() : null, e);
    }

    static async Task<long> Spendable(HttpClient op, Guid id)
        => (await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{id}/wallet")).GetProperty("spendableToman").GetInt64();

    [Theory]
    [InlineData(false, """[{"method":"Wallet","amountToman":49500},{"method":"Cash","amountToman":1000}]""")]
    [InlineData(true, """[{"method":"Wallet","amountToman":49500},{"method":"Cash","amountToman":1000}]""")]
    [InlineData(false, """[{"method":"Wallet","amountToman":50000},{"method":"Cash","amountToman":1000}]""")]
    [InlineData(true, """[{"method":"Wallet","amountToman":50000},{"method":"Cash","amountToman":1000}]""")]
    public async Task Rr3_over_tender_unchanged_settings_is_400_not_409(bool session, string pay)
    {
        var (op, _, pv, cf, cust) = await Setup(session, 60_000, null, On);
        var before = await Spendable(op, cust);
        var token = await PreviewCollectible(op, pv, pay);
        Assert.Equal(50_000, token);
        var (st, _, _) = await Confirm(op, cf, token, pay);
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(before - 50_000, await Spendable(op, cust));
    }

    [Theory]
    [InlineData(false, "off")]
    [InlineData(true, "off")]
    [InlineData(false, "nowallet")]
    [InlineData(true, "nowallet")]
    public async Task Rr3_real_settings_change_between_preview_and_pay_is_409(bool session, string change)
    {
        const string pay = """[{"method":"Wallet","amountToman":50000}]""";
        var (op, mgr, pv, cf, cust) = await Setup(session, 60_000, null, On);
        var before = await Spendable(op, cust);
        var token = await PreviewCollectible(op, pv, pay);
        Assert.Equal(50_000, token);
        await PutRoundingAsync(mgr, change == "off" ? Off : NoWallet);
        var (st, code, _) = await Confirm(op, cf, token, pay);
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.Equal("stale_rounding_preview", code);
        Assert.Equal(before, await Spendable(op, cust));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rr3_real_change_with_over_tender_is_409(bool session)
    {
        const string pay = """[{"method":"Wallet","amountToman":49500},{"method":"Cash","amountToman":1000}]""";
        var (op, mgr, pv, cf, _) = await Setup(session, 60_000, null, On);
        var token = await PreviewCollectible(op, pv, pay);
        await PutRoundingAsync(mgr, Off);
        var (st, code, _) = await Confirm(op, cf, token, pay);
        Assert.Equal(HttpStatusCode.Conflict, st);
        Assert.Equal("stale_rounding_preview", code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rr3B1_preview_collectible_equals_confirmed_collectible(bool session)
    {
        const string pay = """[{"method":"Wallet","amountToman":49000},{"method":"Cash","amountToman":5000}]""";
        var (op, _, pv, cf, cust) = await Setup(session, 49_700, -50_000, On);
        var before = await Spendable(op, cust);
        var token = await PreviewCollectible(op, pv, pay);
        Assert.Equal(50_000, token);
        var (st, _, _) = await Confirm(op, cf, token, pay);
        Assert.Equal(HttpStatusCode.OK, st);
        Assert.Equal(before - 49_000, await Spendable(op, cust));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task pr51_r6_cash_only_non_wallet_rounding_collectible_50000(bool session)
    {
        var (op, _, pv, cf, _) = await Setup(session, 0, null, NoWallet);
        const string pay = """[{"method":"Cash","amountToman":50000}]""";
        var token = await PreviewCollectible(op, pv, pay);
        Assert.Equal(50_000, token);
        var (st, _, body) = await Confirm(op, cf, token, pay);
        Assert.Equal(HttpStatusCode.OK, st);
        var charged = body.TryGetProperty("collectibleToman", out var c)
            ? c.GetInt64()
            : body.GetProperty("invoice").GetProperty("collectibleToman").GetInt64();
        Assert.Equal(50_000, charged);
    }
}