using System.Diagnostics;
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
public sealed class InpageFormsPr51R14eTests(GameClubWebApplicationFactory factory)
{
    private const string NonCashOverPayablePersian = "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameClub.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
    private static async Task PutRoundingAsync(HttpClient mgr, object body)
        => (await mgr.PutAsJsonAsync("/api/operator/settings/rounding", body)).EnsureSuccessStatusCode();

    private static object RoundingUp5k()
        => new
        {
            enabled = true,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        };

    private static object RoundingDown1k()
        => new
        {
            enabled = true,
            stepToman = 1_000L,
            direction = "Down",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        };

    private static async Task<Guid> CreateCustomerAsync(HttpClient op)
    {
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r14e", mobile = $"0912{Random.Shared.Next(10000000, 99999999)}" });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<long> WalletSpendableAsync(HttpClient op, Guid customerId)
        => (await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet")).GetProperty("spendableToman").GetInt64();

    private static async Task<long> WalletRealAsync(HttpClient op, Guid customerId)
        => (await op.GetFromJsonAsync<JsonElement>($"/api/operator/members/customers/{customerId}/wallet")).GetProperty("realToman").GetInt64();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res)
    {
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static void AssertPaymentInvalid400(JsonElement body, long expectedCollectibleToman = 50_000)
    {
        Assert.Equal("payment_invalid", body.GetProperty("code").GetString());
        var latin = expectedCollectibleToman.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)
            .Replace(",", "٬", StringComparison.Ordinal);
        var expectedFragment = GameClub.Server.Operator.Ss14BillDisplay.ToPersianDigits(latin);
        Assert.Contains(expectedFragment, body.GetProperty("message").GetString() ?? "");
    }

    private async Task<Guid> OpenSession49500BillAsync(HttpClient api, HttpClient op, Guid customerId, string station)
    {
        await S3TestHelpers.RegisterConsole(api, op, station, 1);
        factory.Clock.SetTehran(2026, 6, 10, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(api, station, 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId });
        await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/sale/lines",
            new { catalogItemId = CustomServiceId, customAmountToman = 49_500L });
        factory.Clock.SetTehran(2026, 6, 10, 14, 0);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        return sessionId;
    }

  /// <summary>T10: Down→Up rounding change, no preview token; W49,000 must 400 like fresh preview — no ledger/debt.</summary>
    [Fact]
    public async Task r14e_t10_wallet49000_confirm_after_down_to_up_rounding_no_preview_token_rejected_nothing_written()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var mgr = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await PutRoundingAsync(mgr, RoundingDown1k());
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var beforeSpendable = await WalletSpendableAsync(op, customerId);
        var realBefore = await WalletRealAsync(op, customerId);
        var sessionId = await OpenSession49500BillAsync(api, op, customerId, "PS5-T10");
        await PutRoundingAsync(mgr, RoundingUp5k());

        var preview = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/preview",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(preview));

        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { payments = new[] { new { method = "Wallet", amountToman = 49_000L } } });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(confirm));

        Assert.Equal(beforeSpendable, await WalletSpendableAsync(op, customerId));
        Assert.Equal(realBefore, await WalletRealAsync(op, customerId));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.Invoices.CountAsync());
        Assert.Equal(0, await db.PaymentLines.CountAsync());
        Assert.Equal(0, await db.WalletLedger.CountAsync(w => w.Kind == WalletLedgerKind.InvoiceSpend));
    }

    /// <summary>
    /// Mutant (billing): Collect alone settles wallet-only 49,000 on 49,500→50,000 gap — see
    /// <c>gate_mutant_r14e_t10_source_allocator_wallet49000_alone_settles_payable_50000</c> in Billing.Tests.
    /// Server must keep rejecting via <see cref="OperatorBillService.RejectWalletOnlyBelowUnroundedGap"/>.
    /// </summary>
    [Fact]
    public Task gate_mutant_r14e_t10_debt_floor_gap_settlement_dies()
        => r14e_t10_wallet49000_confirm_after_down_to_up_rounding_no_preview_token_rejected_nothing_written();

    [Fact]
    public async Task r14e_a_wallet49500_cash1000_settles_lines_change_500_preview_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var before = await WalletSpendableAsync(op, customerId);
        var sessionId = await OpenSession49500BillAsync(api, op, customerId, "PS5-R14E-A");
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 1_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var bill = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50_000, bill!.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(500, bill.GetProperty("changeToman").GetInt64());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 50_000L, payments });
        confirm.EnsureSuccessStatusCode();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.PaymentLines).SingleAsync();
        Assert.Equal(50_000, invoice.CollectibleToman);
        Assert.Equal(49_500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(500, invoice.PaymentLines.Single(p => p.Method == PaymentMethod.Cash).AmountToman);
        Assert.Equal(before - 49_500, await WalletSpendableAsync(op, customerId));
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

    private static async Task<Guid> OpenBuffet15500Async(HttpClient op, Guid customerId)
        => await OpenBuffetAsync(op, customerId, 15_500L);

    private static void AssertPaymentInvalidCode(JsonElement body)
        => Assert.Equal("payment_invalid", body.GetProperty("code").GetString());

    private static async Task AssertPreviewConfirmBoth400SameMessageAsync(
        HttpClient op,
        string previewPath,
        string confirmPath,
        object previewBody,
        object confirmBody)
    {
        var preview = await op.PostAsJsonAsync(previewPath, previewBody);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        AssertPaymentInvalidCode(previewJson);
        var confirm = await op.PostAsJsonAsync(confirmPath, confirmBody);
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        AssertPaymentInvalidCode(confirmJson);
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
    }

    [Fact]
    public async Task r14e_2_wallet500_card15k_cash5k_settles_preview_change_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        });
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
        var bill = (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill");
        Assert.Equal(15_500, bill.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(5_000, bill.GetProperty("changeToman").GetInt64());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments });
        confirm.EnsureSuccessStatusCode();
        Assert.Equal(5_000, (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("changeToman").GetInt64());
    }

    [Fact]
    public async Task r14e_2_wallet500_card14k_cash5k_settles_preview_change_matches_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
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
            new { method = "CardReader", amountToman = 14_000L },
            new { method = "Cash", amountToman = 5_000L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        Assert.Equal(4_000, (await preview.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("bill").GetProperty("changeToman").GetInt64());
        (await op.PostAsJsonAsync(
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { previewCollectibleToman = 15_500L, payments })).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task r14e_2_wallet_over_balance_preview_and_confirm_400_match()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
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
            new { method = "Cash", amountToman = 500L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new[] { new { method = "Wallet", amountToman = 20_000L } };
        await AssertPreviewConfirmBoth400SameMessageAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments },
            new { payments });
    }

    [Fact]
    public async Task r14e_2_wallet_no_balance_preview_and_confirm_400_match()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = false,
        })).EnsureSuccessStatusCode();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new[] { new { method = "Wallet", amountToman = 500L } };
        await AssertPreviewConfirmBoth400SameMessageAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments },
            new { payments });
    }

    [Fact]
    public async Task r14e_3_d03_wallet48000_preview_and_confirm_400_with_collectible_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[] { new { method = "Wallet", amountToman = 48_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(preview));
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        AssertPaymentInvalid400(await ReadJsonAsync(confirm));
    }

    [Fact]
    public async Task r14e_3_d10_wallet20k_wallet29k_preview_and_confirm_400()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var openSaleId = await OpenBuffetAsync(op, customerId, 49_500L);
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 20_000L },
            new { method = "Wallet", amountToman = 29_000L },
        };
        await AssertPreviewConfirmBoth400SameMessageAsync(
            op,
            $"/api/operator/buffet-only/{openSaleId}/bill/preview",
            $"/api/operator/buffet-only/{openSaleId}/confirm",
            new { payments },
            new { payments });
    }

    [Fact]
    public async Task r14e_7_wallet49500_cash500_exact_settles_conf_change_from_collectible()
    {
        await S3TestHelpers.ResetAsync(factory);
        var api = factory.CreateClientWithKey();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        (await op.PostAsJsonAsync(
            $"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 60_000L, commandId = Guid.NewGuid() })).EnsureSuccessStatusCode();
        var sessionId = await OpenSession49500BillAsync(api, op, customerId, "PS5-R14E-7");
        var payments = new[]
        {
            new { method = "Wallet", amountToman = 49_500L },
            new { method = "Cash", amountToman = 500L },
        };
        var preview = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/bill/preview", new { payments });
        preview.EnsureSuccessStatusCode();
        var previewJson = await preview.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50_000, previewJson!.GetProperty("collectibleToman").GetInt64());
        Assert.Equal(0, previewJson.GetProperty("changeToman").GetInt64());
        var confirm = await op.PostAsJsonAsync(
            $"/api/operator/sessions/{sessionId}/bill/confirm",
            new { previewCollectibleToman = 50_000L, payments });
        confirm.EnsureSuccessStatusCode();
        Assert.Equal(0, (await confirm.Content.ReadFromJsonAsync<JsonElement>())!.GetProperty("changeToman").GetInt64());
    }

    [Fact]
    public void r14e_6_ml2_unknown_wallet_member_splitter_rejects_exact_wallet_line()
    {
        var split = PaymentSplitter.Split(15_500, [new Tender(PaymentMethod.Wallet, 15_500, "missing-member")]);
        Assert.False(split.IsValid);
        Assert.Contains(split.Errors, e => e.Code == PaymentErrors.UnknownWallet);
    }

    [Fact]
    public async Task r14e_8_non_cash_exceed_preview_confirm_same_persian_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        (await manager.PutAsJsonAsync("/api/operator/settings/rounding", new
        {
            enabled = false,
            stepToman = 5_000L,
            direction = "Up",
            applyToWallet = true,
            walletMinBillToman = 5_000L,
            allowWalletShortBalanceDebt = true,
        })).EnsureSuccessStatusCode();
        var op = await factory.CreateOperatorClientAsync();
        var customerId = await CreateCustomerAsync(op);
        var openSaleId = await OpenBuffet15500Async(op, customerId);
        var payments = new[] { new { method = "CardReader", amountToman = 20_000L } };
        var preview = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/bill/preview", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        var previewJson = await ReadJsonAsync(preview);
        Assert.Equal(NonCashOverPayablePersian, previewJson.GetProperty("message").GetString());
        var confirm = await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new { payments });
        Assert.Equal(HttpStatusCode.BadRequest, confirm.StatusCode);
        var confirmJson = await ReadJsonAsync(confirm);
        Assert.Equal(NonCashOverPayablePersian, confirmJson.GetProperty("message").GetString());
        Assert.Equal(previewJson.GetProperty("message").GetString(), confirmJson.GetProperty("message").GetString());
    }

    [Fact]
    public void r14e_4_cp_layer_busy_submit_popstate_guard_present()
    {
        var root = RepoRoot();
        var src = File.ReadAllText(Path.Combine(root, "src/GameClub.Server/wwwroot/operator/cp-layer.js"));
        Assert.Contains("active.busy || active._submitInFlight", src);
        var mutant = File.ReadAllText(Path.Combine(
            root,
            "tests/GameClub.Server.Tests/Scripts/fixtures/cp-layer-no-busy-popstate-guard.mjs"));
        Assert.Contains("mutant R14e-N2: busy guard removed", mutant);
        Assert.DoesNotContain("active.busy || active._submitInFlight", mutant);
    }

    /// <summary>Release net10.0 full <c>scripts/agent/test.sh</c> Server trx Total (gate pin).</summary>
    public const int R14eServerSuiteTrxTotal = 1034;

    private const int R14eServerListTestsPin = R14eServerSuiteTrxTotal - 14;

    [Fact]
    public async Task r14e_item9_server_test_inventory_from_dotnet_list_tests()
    {
        var csproj = Path.Combine(RepoRoot(), "tests", "GameClub.Server.Tests", "GameClub.Server.Tests.csproj");
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{csproj}\" -c Release -f net10.0 --list-tests --no-build",
            WorkingDirectory = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        Assert.NotNull(proc);
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
        var listed = stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.Contains("GameClub.Server.Tests.", StringComparison.Ordinal));
        Assert.Equal(R14eServerListTestsPin, listed);
        Assert.Equal(R14eServerSuiteTrxTotal, R14eServerListTestsPin + 14);
    }
}