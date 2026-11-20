using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class InpageFormsOperatorMoneyCapRoutesTests(GameClubWebApplicationFactory factory)
{
    private const long OverCap = OperatorMoneyValidation.MaxToman + 1;
    private static readonly long FifteenDigits = OperatorMoneyValidation.MaxToman;

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new { name = "Cap", mobile = $"0914{Random.Shared.Next(1000000, 9999999)}" });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static void Assert400Persian(JsonElement body, string expectedCode)
    {
        Assert.Equal(expectedCode, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
        Assert.DoesNotContain("System.", body.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    public sealed record MoneyCapRouteCase(string Name, string ExpectedCode);

    public static TheoryData<MoneyCapRouteCase> MoneyWriteRouteCapCases() =>
    [
        new("top_up_amount", "invalid_amount"),
        new("manager_gift", "invalid_amount"),
        new("opening_real", "invalid_opening"),
        new("opening_gift", "invalid_gift"),
        new("opening_debt", "invalid_opening"),
        new("operator_gift_cap", "invalid_cap"),
        new("debt_floor", "invalid_floor"),
        new("shift_open_float", "invalid_float"),
        new("shift_close_cash", "invalid_money"),
        new("shift_close_card", "invalid_money"),
        new("prepaid_amount", "invalid_amount"),
        new("bill_payment", "payment_invalid"),
        new("game_rate", "invalid_rate"),
        new("level_ladder_hours", "invalid_level"),
        new("wallet_correction_amount", "invalid_amount"),
        new("wallet_correction_gift", "invalid_gift"),
    ];

    [Theory]
    [MemberData(nameof(MoneyWriteRouteCapCases))]
    public async Task money_write_route_rejects_fifteen_digit_overflow(MoneyCapRouteCase route)
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        HttpResponseMessage res;

        switch (route.Name)
        {
            case "top_up_amount":
            {
                var customerId = await CreateCustomerAsync(op);
                res = await op.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/wallet/top-up",
                    new { method = "Cash", amountToman = OverCap, giftToman = 0L, commandId = Guid.NewGuid() });
                break;
            }
            case "manager_gift":
            {
                var customerId = await CreateCustomerAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/wallet/gift",
                    new { giftToman = OverCap });
                break;
            }
            case "opening_real":
            {
                var customerId = await CreateCustomerAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/opening-balance",
                    new { realToman = OverCap });
                break;
            }
            case "opening_gift":
            {
                var customerId = await CreateCustomerAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/opening-balance",
                    new { giftToman = OverCap });
                break;
            }
            case "opening_debt":
            {
                var customerId = await CreateCustomerAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/opening-balance",
                    new { debtToman = OverCap });
                break;
            }
            case "operator_gift_cap":
                res = await mgr.PutAsJsonAsync(
                    "/api/operator/members/settings/operator-gift-cap",
                    new { maxToman = OverCap, maxPercent = 10 });
                break;
            case "debt_floor":
                res = await mgr.PutAsJsonAsync(
                    "/api/operator/members/settings/debt-floor",
                    new { floorToman = -OverCap });
                break;
            case "shift_open_float":
                await S3TestHelpers.EnableShiftsAsync(mgr);
                res = await mgr.PostAsJsonAsync("/api/operator/shift/open", new { openingFloatToman = OverCap });
                break;
            case "shift_close_cash":
                await S3TestHelpers.EnableShiftsAsync(mgr);
                await S3TestHelpers.OpenShiftAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    "/api/operator/shift/close",
                    new
                    {
                        workingDay = new { year = 2026, month = 6, day = 3 },
                        actualCashToman = OverCap,
                        actualCardReaderToman = 0L,
                        sessionActions = Array.Empty<object>(),
                    });
                break;
            case "shift_close_card":
                await S3TestHelpers.EnableShiftsAsync(mgr);
                await S3TestHelpers.OpenShiftAsync(mgr);
                res = await mgr.PostAsJsonAsync(
                    "/api/operator/shift/close",
                    new
                    {
                        workingDay = new { year = 2026, month = 6, day = 3 },
                        actualCashToman = 0L,
                        actualCardReaderToman = OverCap,
                        sessionActions = Array.Empty<object>(),
                    });
                break;
            case "prepaid_amount":
            {
                var api = factory.CreateClientWithKey();
                await S3TestHelpers.RegisterConsole(api, op, "PS5-CAP", 1);
                factory.Clock.SetTehran(2026, 6, 3, 14, 0);
                var sessionId = await S3TestHelpers.StartSession(api, "PS5-CAP", 1);
                res = await op.PostAsJsonAsync(
                    $"/api/operator/sessions/{sessionId}/prepaid/amount",
                    new { amountToman = OverCap });
                break;
            }
            case "bill_payment":
            {
                var api = factory.CreateClientWithKey();
                await S3TestHelpers.RegisterConsole(api, op, "PS5-CAP2", 1);
                factory.Clock.SetTehran(2026, 6, 3, 14, 0);
                var sessionId = await S3TestHelpers.StartSession(api, "PS5-CAP2", 1);
                (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
                res = await op.PostAsJsonAsync(
                    $"/api/operator/sessions/{sessionId}/bill/confirm",
                    new { payments = new[] { new { method = "Cash", amountToman = OverCap } } });
                break;
            }
            case "game_rate":
                res = await mgr.PostAsync(
                    "/api/operator/game-titles",
                    new StringContent(
                        $"{{\"name\":\"T\",\"enabled\":true,\"rates\":[{{\"mode\":\"Offline\",\"playerCount\":1,\"hourlyToman\":{OverCap}}}]}}",
                        Encoding.UTF8,
                        "application/json"));
                break;
            case "level_ladder_hours":
                res = await mgr.PutAsJsonAsync(
                    "/api/operator/members/settings/level-ladder",
                    new[] { new { name = "A", minimumLifetimeHours = OverCap, discountPercent = 0 } });
                break;
            case "wallet_correction_amount":
            case "wallet_correction_gift":
            {
                var customerId = await CreateCustomerAsync(mgr);
                (await mgr.PostAsJsonAsync(
                    $"/api/operator/members/customers/{customerId}/wallet/top-up",
                    new { method = "Cash", amountToman = 1000L, giftToman = 0L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var groupId = await db.WalletLedger.AsNoTracking()
                    .Where(w => w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal && w.TopUpGroupId != null)
                    .Select(w => w.TopUpGroupId!.Value)
                    .FirstAsync();
                res = await mgr.PostAsJsonAsync(
                    $"/api/operator/members/wallet/top-ups/{groupId}/correct",
                    route.Name == "wallet_correction_amount"
                        ? new { newAmountToman = OverCap, newGiftToman = 0L, description = "cap test", commandId = Guid.NewGuid() }
                        : new { newAmountToman = 1000L, newGiftToman = OverCap, description = "cap test", commandId = Guid.NewGuid() });
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(route), route.Name, "unknown route case");
        }

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert400Persian(body, route.ExpectedCode);
    }

    [Fact]
    public async Task top_up_rejects_wallet_total_past_max_toman()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var customerId = await CreateCustomerAsync(mgr);
        var nearMax = FifteenDigits - 50;
        (await mgr.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/opening-balance",
            new { realToman = nearMax })).EnsureSuccessStatusCode();

        var op = await factory.CreateOperatorClientAsync();
        var res = await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100L, giftToman = 0L, commandId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert400Persian(body, "invalid_amount");
    }
}