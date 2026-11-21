using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class OperatorPanelE2eTests(GameClubWebApplicationFactory factory) : IAsyncLifetime
{
    private const string PanelOrigin = "http://panel.test";
    private readonly HttpClient _api = factory.CreateClientWithKey();
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task InitializeAsync()
    {
        await BrowserInstallGate.EnsureChromiumAsync();
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _playwright?.Dispose();
    }

    private static readonly Regex CommandIdUuidRegex = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed class NativeDialogRecorder
    {
        public List<string> Calls { get; } = [];
    }

    private static readonly ConditionalWeakTable<IPage, NativeDialogRecorder> NativeDialogRecorders = new();

    private static void AttachNativeDialogRecorder(IPage page)
    {
        var recorder = new NativeDialogRecorder();
        NativeDialogRecorders.Add(page, recorder);
        page.Dialog += (_, d) => recorder.Calls.Add($"{d.Type}:{d.Message}");
    }

    private static void AssertNoUnexpectedNativeDialogs(IPage page)
    {
        if (NativeDialogRecorders.TryGetValue(page, out var recorder))
        {
            NativeDialogRecorderAssert.AssertNoUnexpectedNativeDialogs(recorder.Calls);
        }
    }

    private static string? ParseCommandIdFromPost(string? postData)
    {
        if (string.IsNullOrWhiteSpace(postData))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(postData);
        return doc.RootElement.TryGetProperty("commandId", out var id) ? id.GetString() : null;
    }

    private static async Task FillCpFieldAsync(IPage page, string fieldName, string value)
    {
        var sel = $"#cpLayer [data-cp-field='{fieldName}']";
        await page.WaitForSelectorAsync(sel);
        await page.FillAsync(sel, value);
    }

    private static Task ClickCpSubmitAsync(IPage page) =>
        page.ClickAsync("#cpLayer [data-cp-role='submit']");

    private static Task ClickCpCancelAsync(IPage page) =>
        page.ClickAsync("#cpLayer [data-cp-role='cancel']");

    private static async Task WaitForCpLayerHiddenAsync(IPage page)
    {
        await page.Locator("#cpLayer").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }

    private static async Task SelectCpRadioAsync(IPage page, string value)
    {
        await page.ClickAsync($"#cpLayer input[type=radio][value='{value}']");
    }

    private static async Task ClickCpPickFirstAsync(IPage page)
    {
        await page.ClickAsync("#cpLayer .cp-pick-row >> nth=0");
    }

    private static async Task FillCpCorrectionFormAsync(IPage page)
    {
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await FillCpFieldAsync(page, "newAmountToman", "90000");
        await FillCpFieldAsync(page, "newGiftToman", "0");
        await FillCpFieldAsync(page, "description", "r4-e2e");
        await ClickCpSubmitAsync(page);
    }

    private static async Task FillWalletTopUpCashAsync(IPage page, string amount = "50000")
    {
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await FillCpFieldAsync(page, "amountToman", amount);
        await ClickCpSubmitAsync(page);
    }

    private static async Task FillC2cPaymentLineAsync(IPage page, string amount = "80000")
    {
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await FillCpFieldAsync(page, "amountToman", amount);
        await ClickCpSubmitAsync(page);
        await WaitForCpLayerHiddenAsync(page);
    }

    private static async Task<string> WaitForBillTextAsync(
        Func<Task<string>> read,
        string expectedSubstring,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var text = await read();
        while (!text.Contains(expectedSubstring, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            text = await read();
        }

        return text;
    }

    private async Task<IPage> NewPanelPageAsync(HttpClient? operatorClient = null, bool stripRandomUuid = false)
    {
        var client = operatorClient ?? factory.CreateClient();
        var context = await _browser!.NewContextAsync();
        if (stripRandomUuid)
        {
            await context.AddInitScriptAsync(
                "() => { if (typeof crypto !== 'undefined') { delete crypto.randomUUID; } }");
        }

        var page = await context.NewPageAsync();
        AttachNativeDialogRecorder(page);
        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var pathAndQuery = request.Url.StartsWith(PanelOrigin, StringComparison.OrdinalIgnoreCase)
                ? request.Url[PanelOrigin.Length..]
                : request.Url;
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), pathAndQuery);
            if (request.Headers.TryGetValue("cookie", out var cookie))
            {
                message.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            var postBytes = request.PostDataBuffer;
            if (request.Method is "POST" or "PUT" or "PATCH")
            {
                if (postBytes is { Length: > 0 })
                {
                    var mediaType = request.Headers.TryGetValue("content-type", out var contentType)
                        ? contentType.Split(';')[0]
                        : "application/json";
                    message.Content = new ByteArrayContent(postBytes);
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
                }
                else
                {
                    message.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
                }
            }

            var response = await client.SendAsync(message);
            var body = await response.Content.ReadAsByteArrayAsync();
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            await route.FulfillAsync(new()
            {
                Status = (int)response.StatusCode,
                BodyBytes = body,
                Headers = headers,
            });
        });
        return page;
    }

    private async Task<IPage> LoginAsync(HttpClient? operatorClient = null, bool stripRandomUuid = false)
    {
        var page = await NewPanelPageAsync(operatorClient, stripRandomUuid);
        await page.GotoAsync($"{PanelOrigin}/operator/index.html");
        await page.FillAsync("#username", "operator");
        await page.FillAsync("#pin", "1111");
        await page.ClickAsync("#loginBtn");
        await page.WaitForSelectorAsync("#grid .tile");
        return page;
    }

    private async Task<HttpClient> LoginOperatorClientAsync(string username = "operator", string pin = "1111")
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username, pin });
        login.EnsureSuccessStatusCode();
        return client;
    }

    [Fact]
    public async Task e2e_s10_card_to_card_stays_pending_until_list_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-a", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-E2E", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-E2E");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-E2E']");
        await page.ClickAsync("#addC2cLineBtn");
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await FillCpFieldAsync(page, "amountToman", "80000");
        await ClickCpSubmitAsync(page);
        await WaitForCpLayerHiddenAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var line = await db.PaymentLines.SingleAsync();
            Assert.Equal(PaymentConfirmationStatus.Pending, line.ConfirmationStatus);
        }

        await page.Locator("#pendingC2cList button").First.ClickAsync();
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if ((await db.PaymentLines.SingleAsync()).ConfirmationStatus == PaymentConfirmationStatus.Confirmed)
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(PaymentConfirmationStatus.Confirmed, (await db.PaymentLines.SingleAsync()).ConfirmationStatus);
        }

        AssertNoUnexpectedNativeDialogs(page);
    }

    [Fact]
    public async Task e2e_s13_manual_settle_option_b_120000()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-MS-E", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-MS-E");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 14, 30));
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);

        var page = await LoginAsync();
        await page.EvaluateAsync("() => refreshManualSettle()");
        await page.FillAsync("#manualSettleList input[type=datetime-local]", "2026-06-03T15:30");
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Bill to end time" }).ClickAsync();
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var detail = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(",", "").Replace(" ", ""),
            "120000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("120000", detail);

        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(i => i.SessionId == sessionId))
            {
                break;
            }

            await Task.Delay(250);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await verifyDb.Invoices.SingleAsync(i => i.SessionId == sessionId);
        Assert.Equal(120_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task e2e_n1_zero_bill_confirm_without_tender()
    {
        await S3TestHelpers.ResetAsync(factory);
        await S3TestHelpers.RegisterPc(_api, "PC-ZERO", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-ZERO");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0, 40);
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var page = await LoginAsync();
        await page.ClickAsync(".tile[data-station-id='PC-ZERO']");
        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 20; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(x => x.SessionId == sessionId))
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var inv = await db.Invoices.SingleAsync(i => i.SessionId == sessionId);
            Assert.Equal(0, inv.CollectibleToman);
            Assert.Empty(inv.PaymentLines);
            Assert.Equal(SessionStatus.Closed, (await db.Sessions.SingleAsync()).Status);
        }
    }

    [Fact]
    public async Task e2e_s3_console_pause_open_bill_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-E2E", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.StartSession(_api, "PS5-E2E");
        Guid sessionId;
        using (var lookup = factory.Services.CreateScope())
        {
            sessionId = await lookup.ServiceProvider.GetRequiredService<AppDbContext>()
                .Sessions.Where(s => s.StationId == "PS5-E2E").Select(s => s.Id).SingleAsync();
        }

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.WaitForSelectorAsync(".tile[data-station-id='PS5-E2E']:text('Playing')");
        await page.ClickAsync(".tile[data-station-id='PS5-E2E']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        await page.ClickAsync("#pauseBtn");
        await page.WaitForSelectorAsync(".tile[data-station-id='PS5-E2E']:text('Paused')");
        await page.ClickAsync(".tile[data-station-id='PS5-E2E']");
        factory.Clock.SetTehran(2026, 6, 3, 14, 50);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/resume", null)).EnsureSuccessStatusCode();
        factory.Clock.SetTehran(2026, 6, 3, 15, 20);
        (await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null)).EnsureSuccessStatusCode();
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-E2E']");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(110_000, (await db.PendingBills.SingleAsync()).CollectibleToman);
        }

        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 20; i++)
        {
            using var waitScope = factory.Services.CreateScope();
            if (await waitScope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AnyAsync())
            {
                break;
            }

            await Task.Delay(250);
        }

        using var confirmScope = factory.Services.CreateScope();
        var confirmDb = confirmScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(110_000, (await confirmDb.Invoices.SingleAsync()).CollectibleToman);
    }

    [Fact]
    public async Task pr29_r1_confirm_session_bill_not_leftover_buffet_only_context()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R29A", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R29A", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.ClickAsync("#buffetCloseBtn");

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await page.ClickAsync(".tile[data-station-id='PS5-R29A']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        await page.ClickAsync("#openBillBtn");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var detailBefore = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "بوفه15000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("بوفه15000", detailBefore);

        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 40; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(x => x.SessionId == sessionId))
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sessionInvoice = await db.Invoices.SingleAsync(x => x.SessionId == sessionId);
            Assert.Equal(125_000, sessionInvoice.CollectibleToman);
            Assert.False(await db.Invoices.AnyAsync(x => x.SessionId == null && x.BuffetGrossToman == 20_000));
            var buffetOnly = await db.OpenSales.Include(o => o.Lines).SingleAsync(o => o.IsBuffetOnly);
            Assert.Single(buffetOnly.Lines);
        }
    }

    [Fact]
    public async Task pr29_r1_remove_sale_line_refreshes_visible_session_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R29B", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R29B", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-R29B']");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var before = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "بوفه15000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("بوفه15000", before);

        await page.ClickAsync("#openBuffetFromBillBtn");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.WaitForSelectorAsync("#buffetSaleLines .sale-line");
        var deleteDone = page.WaitForResponseAsync(r =>
            r.Request.Method == "DELETE" && r.Url.Contains("/sale/lines/", StringComparison.Ordinal));
        var billRefresh = page.WaitForResponseAsync(r =>
            r.Request.Method == "GET"
            && r.Url.Contains("/sessions/", StringComparison.Ordinal)
            && r.Url.Contains("/bill", StringComparison.Ordinal)
            && !r.Url.Contains("/bill/", StringComparison.Ordinal)
            && r.Ok);
        await page.Locator("#buffetSaleLines button", new() { HasText = "حذف" }).ClickAsync();
        await deleteDone;
        var billResponse = await billRefresh;
        var billBody = await billResponse.JsonAsync<JsonElement>();
        Assert.Equal(0, billBody.GetProperty("buffetGrossToman").GetInt64());
        Assert.Equal(110_000, billBody.GetProperty("collectibleToman").GetInt64());

        var afterNorm = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=110000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("بوفه0", afterNorm);
        Assert.DoesNotContain("بوفه15000", afterNorm);
        Assert.Contains("=110000", afterNorm);
    }

    [Fact]
    public async Task pr29_r2_buffet_only_checkout_remove_refreshes_visible_total()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        var chipsCat = page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" });
        await chipsCat.ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").WaitForAsync();
        var drinksCat = page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" });
        await drinksCat.ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" }).Locator(".cat-item", new() { HasText = "نوشابه" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").Nth(1).WaitForAsync();
        var checkoutDone = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.Contains("/checkout", StringComparison.Ordinal) && r.Ok);
        await page.ClickAsync("#buffetCheckoutBtn");
        await checkoutDone;
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var before = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=35000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=35000", before);
        Assert.Contains("بوفه35000", before);

        var deleteDone = page.WaitForResponseAsync(r =>
            r.Request.Method == "DELETE" && r.Url.Contains("/buffet-only/", StringComparison.Ordinal));
        var billRefresh = page.WaitForResponseAsync(r =>
            r.Request.Method == "GET"
            && r.Url.Contains("/buffet-only/", StringComparison.Ordinal)
            && !r.Url.Contains("/lines/", StringComparison.Ordinal)
            && r.Ok);
        await page.Locator("#buffetSaleLines button", new() { HasText = "حذف" }).Last.ClickAsync();
        await deleteDone;
        var billResponse = await billRefresh;
        var billBody = await billResponse.JsonAsync<JsonElement>();
        Assert.Equal(20_000, billBody.GetProperty("bill").GetProperty("collectibleToman").GetInt64());

        var after = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=20000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=20000", after);
        Assert.Contains("بوفه20000", after);
        Assert.DoesNotContain("35000", after);
        Assert.Equal("", (await page.TextContentAsync("#error")) ?? "");
    }

    [Fact]
    public async Task pr29_r2_cross_context_remove_keeps_buffet_only_bill_then_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R29C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R29C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-R29C']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        await page.ClickAsync("#openBillBtn");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var sessionBillBefore = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=110000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=110000", sessionBillBefore);

        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").WaitForAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" }).Locator(".cat-item", new() { HasText = "چای" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").Nth(1).WaitForAsync();
        var checkoutDone = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.Contains("/checkout", StringComparison.Ordinal) && r.Ok);
        await page.ClickAsync("#buffetCheckoutBtn");
        await checkoutDone;
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        var buffetBeforeRemove = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=30000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=30000", buffetBeforeRemove);

        await page.Locator("#buffetSaleLines button", new() { HasText = "حذف" }).First.ClickAsync();
        await page.WaitForResponseAsync(r =>
            r.Request.Method == "GET"
            && r.Url.Contains("/buffet-only/", StringComparison.Ordinal)
            && r.Ok);
        var buffetAfterRemove = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=10000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=10000", buffetAfterRemove);
        Assert.Contains("بوفه10000", buffetAfterRemove);
        Assert.DoesNotContain("=110000", buffetAfterRemove);
        Assert.Equal("", (await page.TextContentAsync("#error")) ?? "");

        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 40; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(x => x.SessionId == null && x.BuffetGrossToman == 10_000))
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var buffetInvoice = await db.Invoices.SingleAsync(x => x.SessionId == null && x.BuffetGrossToman == 10_000);
            Assert.Equal(10_000, buffetInvoice.CollectibleToman);
            Assert.False(await db.Invoices.AnyAsync(x => x.SessionId == sessionId));
        }
    }

    [Fact]
    public async Task pr29_r2_playing_session_selected_buffet_only_remove_no_session_bill_error()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R29D", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.StartSession(_api, "PS5-R29D", 1);

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-R29D']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").WaitForAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" }).Locator(".cat-item", new() { HasText = "نوشابه" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").Nth(1).WaitForAsync();
        var checkoutDone = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.Contains("/checkout", StringComparison.Ordinal) && r.Ok);
        await page.ClickAsync("#buffetCheckoutBtn");
        await checkoutDone;
        await page.WaitForSelectorAsync("#bill:not(.hidden)");

        await page.Locator("#buffetSaleLines button", new() { HasText = "حذف" }).Last.ClickAsync();
        await page.WaitForResponseAsync(r =>
            r.Request.Method == "GET"
            && r.Url.Contains("/buffet-only/", StringComparison.Ordinal)
            && r.Ok);

        Assert.Equal("", (await page.TextContentAsync("#error")) ?? "");
        var detail = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "=20000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("=20000", detail);
        Assert.DoesNotContain("No bill", detail);
    }

    [Fact]
    public async Task cu15_ui_buffet_panel_create_before_checkout_links_open_sale_not_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-CU15A", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-CU15A", 1);
        var tag = Guid.NewGuid().ToString("N")[..7];
        var stationMobile = "0912" + tag;
        var buffetMobile = "0913" + tag;
        var stationCustomer = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "station-hold",
            mobile = stationMobile,
            linkSessionId = sessionId,
        });
        stationCustomer.EnsureSuccessStatusCode();
        var stationCustomerId = (await stationCustomer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-CU15A']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" })
            .Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").First.WaitForAsync();
        var drinksCat = page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" });
        await drinksCat.ClickAsync();
        await drinksCat.Locator(".cat-item", new() { HasText = "چای" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").Nth(1).WaitForAsync();

        await page.EvaluateAsync("() => { document.querySelector('#customerPanel details').open = true; }");
        await page.FillAsync("#customerName", "buffet-x");
        await page.FillAsync("#customerMobile", buffetMobile);
        var createRequest = page.WaitForRequestAsync(r =>
            r.Method == "POST" && r.Url.Contains("/api/operator/customers", StringComparison.Ordinal));
        var createResponse = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.Contains("/api/operator/customers", StringComparison.Ordinal) && r.Ok);
        await page.ClickAsync("#customerCreateBtn");
        var posted = await createRequest;
        await createResponse;
        await page.WaitForSelectorAsync("#customerChip:not(.hidden)");
        var body = JsonDocument.Parse(posted.PostData ?? "{}").RootElement;
        Assert.True(body.TryGetProperty("linkSessionId", out var lsEl));
        Assert.Equal(JsonValueKind.Null, lsEl.ValueKind);
        Assert.True(body.TryGetProperty("linkOpenSaleId", out var saleEl) && saleEl.ValueKind == JsonValueKind.String);
        var linkedSaleId = saleEl.GetGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.Equal(stationCustomerId, session.CustomerId);
        }

        var saleDetail = await op.GetFromJsonAsync<JsonElement>($"/api/operator/buffet-only/{linkedSaleId}");
        Assert.Equal("buffet-x", saleDetail.GetProperty("linkedCustomer").GetProperty("name").GetString());

        await page.ClickAsync("#buffetCheckoutBtn");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");
        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 40; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(x => x.SessionId == null && x.BuffetGrossToman == 30_000))
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync(x => x.SessionId == null && x.BuffetGrossToman == 30_000);
            Assert.Equal("buffet-x", invoice.Customer!.Name);
            Assert.Equal(30_000, invoice.CollectibleToman);
        }
    }

    [Fact]
    public async Task cu15_ui_buffet_checkout_chip_stays_with_bill_when_session_tile_clicked()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-CU15B", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-CU15B", 1);
        var tag = Guid.NewGuid().ToString("N")[..7];
        var stationY = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "station-y",
            mobile = "0912" + tag,
            linkSessionId = sessionId,
        });
        stationY.EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" })
            .Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").First.WaitForAsync();
        var drinksCat2 = page.Locator("#buffetCategories .cat", new() { HasText = "نوشیدنی" });
        await drinksCat2.ClickAsync();
        await drinksCat2.Locator(".cat-item", new() { HasText = "چای" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").Nth(1).WaitForAsync();
        await page.ClickAsync("#buffetCheckoutBtn");
        await page.WaitForSelectorAsync("#bill:not(.hidden)");

        await page.EvaluateAsync("() => { document.querySelector('#customerPanel details').open = true; }");
        await page.FillAsync("#customerName", "buffet-x");
        await page.FillAsync("#customerMobile", "0913" + tag);
        await page.ClickAsync("#customerCreateBtn");
        await page.WaitForSelectorAsync("#customerChip:not(.hidden)");
        var chipBefore = await page.TextContentAsync("#customerChip");
        Assert.Contains("buffet-x", chipBefore);

        await page.ClickAsync("#buffetCloseBtn");
        await page.ClickAsync(".tile[data-station-id='PS5-CU15B']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        var chipAfter = await page.TextContentAsync("#customerChip");
        Assert.Contains("buffet-x", chipAfter);
        Assert.DoesNotContain("station-y", chipAfter!);
        var billDetail = await WaitForBillTextAsync(
            async () => (await page.TextContentAsync("#billDetail"))!.Replace(" ", ""),
            "بوفه30000",
            TimeSpan.FromSeconds(10));
        Assert.Contains("بوفه30000", billDetail);

        await page.ClickAsync("#confirmBtn");
        for (var i = 0; i < 40; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await db.Invoices.AnyAsync(x => x.SessionId == null && x.BuffetGrossToman == 30_000))
            {
                break;
            }

            await Task.Delay(250);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync(x => x.SessionId == null && x.BuffetGrossToman == 30_000);
            Assert.Equal("buffet-x", invoice.Customer!.Name);
            Assert.Equal(30_000, invoice.CollectibleToman);
            Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync(s => s.Id == sessionId)).Status);
        }
    }

    [Fact]
    public async Task cu15_ui_buffet_panel_open_no_checkout_station_click_links_session_not_sale()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-CU15C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-CU15C", 1);
        var tag = Guid.NewGuid().ToString("N")[..7];
        var priorMobile = "0912" + tag;
        var replaceMobile = "0913" + tag;
        var priorCustomer = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "station-y3",
            mobile = priorMobile,
            linkSessionId = sessionId,
        });
        priorCustomer.EnsureSuccessStatusCode();
        var priorCustomerId = (await priorCustomer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='buffet-only']");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" }).ClickAsync();
        await page.Locator("#buffetCategories .cat", new() { HasText = "خوراکی" })
            .Locator(".cat-item", new() { HasText = "چیپس" }).ClickAsync();
        await page.Locator("#buffetSaleLines .sale-line").First.WaitForAsync();

        await page.ClickAsync(".tile[data-station-id='PS5-CU15C']");
        await page.WaitForSelectorAsync("#sessionActions:not(.hidden)");
        await page.WaitForFunctionAsync("() => document.getElementById('buffetPanel').classList.contains('hidden')");
        await page.WaitForFunctionAsync(
            "() => (document.getElementById('customerChip')?.textContent ?? '').includes('station-y3')",
            null,
            new() { Timeout = 10_000 });
        var chip = await page.TextContentAsync("#customerChip");
        Assert.Contains("station-y3", chip!);

        var pickResp = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "pick-me",
            mobile = replaceMobile,
        });
        pickResp.EnsureSuccessStatusCode();

        await page.EvaluateAsync("() => { document.querySelector('#customerPanel details').open = true; }");
        var linkRequest = page.WaitForRequestAsync(r =>
            r.Method == "PUT" && r.Url.Contains("/api/operator/sessions/", StringComparison.Ordinal) && r.Url.Contains("/customer", StringComparison.Ordinal));
        var linkResponse = page.WaitForResponseAsync(r =>
            r.Request.Method == "PUT" && r.Request.Url.Contains("/api/operator/sessions/", StringComparison.Ordinal) && r.Request.Url.Contains("/customer", StringComparison.Ordinal) && r.Ok);
        await page.FillAsync("#customerSearch", replaceMobile);
        await page.Locator("#customerSearchResults .customer-hit", new() { HasText = "pick-me" }).ClickAsync();
        var posted = await linkRequest;
        await linkResponse;
        Assert.Contains(sessionId.ToString(), posted.Url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/buffet-only/", posted.Url, StringComparison.Ordinal);

        var createMobile = "0914" + tag;
        await page.FillAsync("#customerName", "session-z");
        await page.FillAsync("#customerMobile", createMobile);
        var createRequest = page.WaitForRequestAsync(r =>
            r.Method == "POST" && r.Url.Contains("/api/operator/customers", StringComparison.Ordinal));
        var createResponse = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST" && r.Url.Contains("/api/operator/customers", StringComparison.Ordinal) && r.Ok);
        await page.ClickAsync("#customerCreateBtn");
        var createPosted = await createRequest;
        await createResponse;
        var body = JsonDocument.Parse(createPosted.PostData ?? "{}").RootElement;
        Assert.True(body.TryGetProperty("linkSessionId", out var lsEl) && lsEl.ValueKind == JsonValueKind.String);
        Assert.Equal(sessionId, lsEl.GetGuid());
        Assert.True(body.TryGetProperty("linkOpenSaleId", out var saleEl));
        Assert.Equal(JsonValueKind.Null, saleEl.ValueKind);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
            Assert.Equal("session-z", (await db.Customers.AsNoTracking().SingleAsync(c => c.Id == session.CustomerId!.Value)).Name);
            Assert.NotEqual(priorCustomerId, session.CustomerId);
            var buffetOnlyOpen = await db.OpenSales.AsNoTracking()
                .Where(o => o.IsBuffetOnly && o.SessionId == null)
                .ToListAsync();
            Assert.NotEmpty(buffetOnlyOpen);
            Assert.All(buffetOnlyOpen, o => Assert.Null(o.CustomerId));
        }
    }

    [Fact]
    public async Task s3b_panel_buffet_icon_adds_line_without_stopping_session()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-BUF", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-BUF", 1);
        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshGrid()");
        await page.WaitForSelectorAsync(".tile[data-station-id='PS5-BUF'] .buffet-icon");
        await page.ClickAsync(".tile[data-station-id='PS5-BUF'] .buffet-icon");
        await page.WaitForSelectorAsync("#buffetPanel:not(.hidden)");
        await page.ClickAsync("#buffetCategories .cat");
        await page.ClickAsync("#buffetCategories .cat-item >> nth=0");
        await page.WaitForSelectorAsync("#buffetSaleLines .sale-line");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync(s => s.Id == sessionId)).Status);
        Assert.True(await db.OpenSaleLines.AnyAsync());
    }

    [Fact]
    public async Task e2e_ss4_fix_r1_stale_version_transfer_refreshes_grid_immediately()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "STALE-A", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "STALE-B", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "STALE-C", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "STALE-A", 1);
        var page = await LoginAsync(op);
        await page.ClickAsync(".tile[data-station-id='STALE-A']");
        await page.WaitForSelectorAsync("#transferBtn:not(.hidden)");
        (await S3TestHelpers.TransferAsync(op, sessionId, "STALE-B")).EnsureSuccessStatusCode();
        var gridGets = new List<DateTimeOffset>();
        page.Request += (_, r) =>
        {
            if (r.Method == "GET" && r.Url.Contains("/api/operator/grid", StringComparison.Ordinal))
            {
                gridGets.Add(DateTimeOffset.UtcNow);
            }
        };
        var t0 = DateTimeOffset.UtcNow;
        await page.ClickAsync("#transferBtn");
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await SelectCpRadioAsync(page, "STALE-C");
        await ClickCpSubmitAsync(page);
        var deadline = t0 + TimeSpan.FromSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && !gridGets.Any(g => g >= t0))
        {
            await Task.Delay(50);
        }

        Assert.Contains(gridGets, g => g >= t0 && g - t0 < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task e2e_ss4_fix_r1_operator_pwa_loads_signalr_client()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var page = await LoginAsync(op);
        var signalRLoaded = await page.EvaluateAsync<bool>("() => typeof signalR !== 'undefined'");
        Assert.True(signalRLoaded);
    }

    [Fact]
    public async Task e2e_ss4_console_transfer_source_free_target_playing()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-FROM", 1);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-TO", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        await S3TestHelpers.StartSession(_api, "PS5-FROM", 1);
        var page = await LoginAsync(op);
        await page.ClickAsync(".tile[data-station-id='PS5-FROM']");
        await page.WaitForSelectorAsync("#transferBtn:not(.hidden)");
        var transferTask = page.WaitForResponseAsync(r =>
            r.Url.Contains("/transfer", StringComparison.Ordinal) && r.Request.Method == "POST");
        await page.ClickAsync("#transferBtn");
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await SelectCpRadioAsync(page, "PS5-TO");
        await ClickCpSubmitAsync(page);
        var transferResponse = await transferTask;
        Assert.True(transferResponse.Ok, await transferResponse.TextAsync());
        await page.EvaluateAsync("() => refreshGrid()");
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.tile')].some(t => t.dataset.stationId === 'PS5-TO' && t.dataset.status === 'Playing')",
            null,
            new() { Timeout = 5000 });
        await page.WaitForFunctionAsync(
            "() => [...document.querySelectorAll('.tile')].some(t => t.dataset.stationId === 'PS5-FROM' && t.dataset.status === 'Free')",
            null,
            new() { Timeout = 5000 });
    }

    [Fact]
    public async Task pr37_fix_r4_wallet_top_up_uses_fallback_command_id_and_retries_same_id()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R4TU", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R4TU", 1);
        var tag = Guid.NewGuid().ToString("N")[..7];
        var mobile = "0912" + tag;
        (await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "r4-topup",
            mobile,
            linkSessionId = sessionId,
        })).EnsureSuccessStatusCode();

        var page = await LoginAsync(op, stripRandomUuid: true);
        Assert.False(await page.EvaluateAsync<bool>(
            "() => typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'"));

        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-R4TU']");
        await page.WaitForSelectorAsync("#walletTopUpCash:not([disabled])");

        var topUpPosts = new List<string?>();
        var topUpAttempts = 0;
        await page.RouteAsync(
            url => url.Contains("/wallet/top-up", StringComparison.Ordinal)
                && !url.Contains("/wallet/top-ups", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                topUpAttempts++;
                topUpPosts.Add(route.Request.PostData);
                if (topUpAttempts == 1)
                {
                    await route.FulfillAsync(new()
                    {
                        Status = 503,
                        ContentType = "application/json",
                        Body = """{"code":"transient","message":"fail"}""",
                    });
                    return;
                }

                await route.FallbackAsync();
            });

        var firstFail = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST"
            && r.Url.Contains("/wallet/top-up", StringComparison.Ordinal)
            && !r.Url.Contains("/wallet/top-ups", StringComparison.Ordinal)
            && r.Status == 503);
        await page.ClickAsync("#walletTopUpCash");
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await FillCpFieldAsync(page, "amountToman", "50000");
        await ClickCpSubmitAsync(page);
        await firstFail;

        Assert.Equal(1, topUpAttempts);
        Assert.Single(topUpPosts);
        var firstId = ParseCommandIdFromPost(topUpPosts[0]);
        Assert.NotNull(firstId);
        Assert.Matches(CommandIdUuidRegex, firstId);

        var successResponse = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST"
            && r.Url.Contains("/wallet/top-up", StringComparison.Ordinal)
            && !r.Url.Contains("/wallet/top-ups", StringComparison.Ordinal)
            && r.Ok);
        await FillCpFieldAsync(page, "amountToman", "50000");
        await ClickCpSubmitAsync(page);
        var ok = await successResponse;
        Assert.True(ok.Ok);
        await WaitForCpLayerHiddenAsync(page);

        Assert.Equal(2, topUpAttempts);
        Assert.Equal(2, topUpPosts.Count);
        var secondId = ParseCommandIdFromPost(topUpPosts[1]);
        Assert.NotNull(secondId);
        Assert.Matches(CommandIdUuidRegex, secondId);
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task pr37_fix_r4_wallet_correction_uses_fallback_command_id_and_retries_same_id()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman = 500_000L, maxPercent = 100 })).EnsureSuccessStatusCode();

        await S3TestHelpers.RegisterConsole(_api, op, "PS5-R4CR", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-R4CR", 1);
        var tag = Guid.NewGuid().ToString("N")[..7];
        var mobile = "0913" + tag;
        var created = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name = "r4-corr",
            mobile,
            linkSessionId = sessionId,
        });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            new { method = "Cash", amountToman = 100_000L, giftToman = 0L, commandId = Guid.NewGuid() }))
            .EnsureSuccessStatusCode();

        var page = await LoginAsync(op, stripRandomUuid: true);
        Assert.False(await page.EvaluateAsync<bool>(
            "() => typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function'"));

        await page.EvaluateAsync("() => refreshGrid()");
        await page.ClickAsync(".tile[data-station-id='PS5-R4CR']");
        await page.WaitForSelectorAsync("#walletCorrectBtn:not([disabled])");

        var correctPosts = new List<string?>();
        var correctAttempts = 0;
        await page.RouteAsync(
            url => url.Contains("/wallet/top-ups/", StringComparison.Ordinal)
                && url.Contains("/correct", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                correctAttempts++;
                correctPosts.Add(route.Request.PostData);
                if (correctAttempts == 1)
                {
                    await route.FulfillAsync(new()
                    {
                        Status = 503,
                        ContentType = "application/json",
                        Body = """{"code":"transient","message":"fail"}""",
                    });
                    return;
                }

                await route.FallbackAsync();
            });

        var firstFail = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST"
            && r.Url.Contains("/correct", StringComparison.Ordinal)
            && r.Status == 503);
        await page.ClickAsync("#walletCorrectBtn");
        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await ClickCpPickFirstAsync(page);
        await FillCpCorrectionFormAsync(page);
        await firstFail;

        Assert.Equal(1, correctAttempts);
        Assert.Single(correctPosts);
        var firstId = ParseCommandIdFromPost(correctPosts[0]);
        Assert.NotNull(firstId);
        Assert.Matches(CommandIdUuidRegex, firstId);

        var successResponse = page.WaitForResponseAsync(r =>
            r.Request.Method == "POST"
            && r.Url.Contains("/correct", StringComparison.Ordinal)
            && r.Ok);
        await FillCpCorrectionFormAsync(page);
        var ok = await successResponse;
        Assert.True(ok.Ok);

        Assert.Equal(2, correctAttempts);
        Assert.Equal(2, correctPosts.Count);
        var secondId = ParseCommandIdFromPost(correctPosts[1]);
        Assert.NotNull(secondId);
        Assert.Matches(CommandIdUuidRegex, secondId);
        Assert.Equal(firstId, secondId);
    }

    [Fact]
    public async Task pr37_fix_r5_wallet_pending_confirm_double_click_sends_one_request()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        (await mgr.PutAsJsonAsync("/api/operator/members/settings/operator-gift-cap",
            new { maxToman = 500_000L, maxPercent = 100 })).EnsureSuccessStatusCode();
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r5w", new { label = "w" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r5w", mobile = "0912" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(80_000, 0, "e2e-r5w", "dbl"))).EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        var confirmAttempts = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                confirmAttempts++;
                slowEntered.TrySetResult();
                await Task.Delay(800);
                await route.FallbackAsync();
            });

        var confirmBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var firstClick = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await confirmBtn.ClickAsync(new() { Force = true });
        await firstClick;
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingWalletList li')");

        Assert.Equal(1, confirmAttempts);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var wallet = await db.CustomerMembers.AsNoTracking().SingleAsync(m => m.CustomerId == customerId);
        Assert.Equal(80_000, wallet.RealToman);
        Assert.Equal(1, await db.WalletLedger.CountAsync(w =>
            w.CustomerId == customerId && w.Kind == WalletLedgerKind.TopUpReal));
    }

    [Fact]
    public async Task pr37_fix_r5_wallet_pending_confirm_failure_reenables_button_and_shows_error()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r5wf", new { label = "wf" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r5wf", mobile = "0913" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(60_000, 0, "e2e-r5wf", "fail"))).EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            route => route.FulfillAsync(new()
            {
                Status = 503,
                ContentType = "application/json",
                Body = """{"code":"temp_fail","message":"خطای موقت سرور"}""",
            }));

        var confirmBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.length > 0");

        Assert.True(await confirmBtn.IsEnabledAsync());
        var errText = await page.TextContentAsync("#error");
        Assert.False(string.IsNullOrWhiteSpace(errText));
        Assert.Contains("خطا", errText!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task pr37_fix_r5_session_payment_pending_confirm_double_click_sends_one_request()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-r5", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-R5C2C", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-R5C2C");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-R5C2C']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        var confirmAttempts = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/api/operator/payments/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                confirmAttempts++;
                slowEntered.TrySetResult();
                await Task.Delay(800);
                await route.FallbackAsync();
            });

        var confirmBtn = page.Locator("#pendingC2cList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var firstClick = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await confirmBtn.ClickAsync(new() { Force = true });
        await firstClick;

        for (var i = 0; i < 40; i++)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if ((await db.PaymentLines.SingleAsync()).ConfirmationStatus == PaymentConfirmationStatus.Confirmed)
            {
                break;
            }

            await Task.Delay(250);
        }

        Assert.Equal(1, confirmAttempts);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(PaymentConfirmationStatus.Confirmed, (await db.PaymentLines.SingleAsync()).ConfirmationStatus);
        }
    }

    [Fact]
    public async Task pr37_fix_r5b_wallet_pending_confirm_timeout_reenables_button_and_shows_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r5b-to", new { label = "to" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r5bto", mobile = "0914" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(50_000, 0, "e2e-r5b-to", "hang"))).EnsureSuccessStatusCode();

        var context = await _browser!.NewContextAsync();
        await context.AddInitScriptAsync("window.__pendingActionTimeoutMs = 1500;");
        var page = await context.NewPageAsync();
        AttachNativeDialogRecorder(page);
        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var pathAndQuery = request.Url.StartsWith(PanelOrigin, StringComparison.OrdinalIgnoreCase)
                ? request.Url[PanelOrigin.Length..]
                : request.Url;
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), pathAndQuery);
            if (request.Headers.TryGetValue("cookie", out var cookie))
            {
                message.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            var postBytes = request.PostDataBuffer;
            if (request.Method is "POST" or "PUT" or "PATCH")
            {
                if (postBytes is { Length: > 0 })
                {
                    var mediaType = request.Headers.TryGetValue("content-type", out var contentType)
                        ? contentType.Split(';')[0]
                        : "application/json";
                    message.Content = new ByteArrayContent(postBytes);
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
                }
                else
                {
                    message.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
                }
            }

            var response = await op.SendAsync(message);
            var body = await response.Content.ReadAsByteArrayAsync();
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            await route.FulfillAsync(new()
            {
                Status = (int)response.StatusCode,
                BodyBytes = body,
                Headers = headers,
            });
        });
        await page.GotoAsync($"{PanelOrigin}/operator/index.html");
        await page.FillAsync("#username", "operator");
        await page.FillAsync("#pin", "1111");
        await page.ClickAsync("#loginBtn");
        await page.WaitForSelectorAsync("#grid .tile");

        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            _ => Task.CompletedTask);

        var confirmBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('زمان درخواست تمام شد')",
            null,
            new() { Timeout = 10_000 });

        Assert.True(await confirmBtn.IsEnabledAsync());
        var errText = await page.TextContentAsync("#error");
        Assert.Contains("زمان درخواست تمام شد", errText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task pr37_fix_r5b_session_payment_pending_confirm_network_abort_reenables_button_and_shows_message()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-r5b", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-R5B", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-R5B");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-R5B']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        await page.RouteAsync(
            url => url.Contains("/api/operator/payments/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            route => route.AbortAsync());

        var confirmBtn = page.Locator("#pendingC2cList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('خطای شبکه')",
            null,
            new() { Timeout = 10_000 });

        Assert.True(await confirmBtn.IsEnabledAsync());
        var errText = await page.TextContentAsync("#error");
        Assert.Contains("خطای شبکه", errText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task pr37_fix_r6_background_poll_and_prepaid_refresh_preserves_error_banner()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r6-bg", new { label = "bg" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6bg", mobile = "0915" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(40_000, 0, "e2e-r6-bg", "bg"))).EnsureSuccessStatusCode();

        var context = await _browser!.NewContextAsync();
        await context.AddInitScriptAsync(
            "window.__operatorPollIntervalMs = 400; window.__prepaidRefreshIntervalMs = 400;");
        var page = await context.NewPageAsync();
        AttachNativeDialogRecorder(page);
        await page.RouteAsync("**/*", async route =>
        {
            var request = route.Request;
            var pathAndQuery = request.Url.StartsWith(PanelOrigin, StringComparison.OrdinalIgnoreCase)
                ? request.Url[PanelOrigin.Length..]
                : request.Url;
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), pathAndQuery);
            if (request.Headers.TryGetValue("cookie", out var cookie))
            {
                message.Headers.TryAddWithoutValidation("Cookie", cookie);
            }

            var postBytes = request.PostDataBuffer;
            if (request.Method is "POST" or "PUT" or "PATCH")
            {
                if (postBytes is { Length: > 0 })
                {
                    var mediaType = request.Headers.TryGetValue("content-type", out var contentType)
                        ? contentType.Split(';')[0]
                        : "application/json";
                    message.Content = new ByteArrayContent(postBytes);
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
                }
                else
                {
                    message.Content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
                }
            }

            var response = await op.SendAsync(message);
            var body = await response.Content.ReadAsByteArrayAsync();
            var headers = response.Headers
                .Concat(response.Content.Headers)
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            await route.FulfillAsync(new()
            {
                Status = (int)response.StatusCode,
                BodyBytes = body,
                Headers = headers,
            });
        });
        await page.GotoAsync($"{PanelOrigin}/operator/index.html");
        await page.FillAsync("#username", "operator");
        await page.FillAsync("#pin", "1111");
        await page.ClickAsync("#loginBtn");
        await page.WaitForSelectorAsync("#grid .tile");

        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            route => route.AbortAsync());

        var confirmBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('خطای شبکه')",
            null,
            new() { Timeout = 10_000 });

        await page.WaitForTimeoutAsync(1_200);
        var errText = await page.TextContentAsync("#error");
        Assert.Contains("خطای شبکه", errText ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task pr37_fix_r6_wallet_pending_list_rerender_keeps_buttons_disabled_until_confirm_finishes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r6w", new { label = "w" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6w", mobile = "0916" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(70_000, 0, "e2e-r6w", "hold"))).EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        var confirmAttempts = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                confirmAttempts++;
                slowEntered.TrySetResult();
                await Task.Delay(900);
                await route.FallbackAsync();
            });

        var row = page.Locator("#pendingWalletList li").First;
        var confirmBtn = row.GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var rejectBtn = row.GetByRole(AriaRole.Button, new() { Name = "رد" });
        var confirmTask = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForFunctionAsync(
            "() => { const li = document.querySelector('#pendingWalletList li'); if (!li) return false; const btns = li.querySelectorAll('button'); return btns.length >= 2 && [...btns].every(b => b.disabled); }");

        Assert.False(await confirmBtn.IsEnabledAsync());
        Assert.False(await rejectBtn.IsEnabledAsync());
        await rejectBtn.ClickAsync(new() { Force = true });
        await confirmBtn.ClickAsync(new() { Force = true });
        await confirmTask;
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingWalletList li')");
        Assert.Equal(1, confirmAttempts);
    }

    [Fact]
    public async Task pr37_fix_r6_c2c_pending_list_rerender_keeps_buttons_disabled_until_confirm_finishes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-r6c2c", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-R6C2C", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-R6C2C");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-R6C2C']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        var confirmAttempts = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/api/operator/payments/", StringComparison.Ordinal)
                && url.Contains("/confirm", StringComparison.Ordinal),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                confirmAttempts++;
                slowEntered.TrySetResult();
                await Task.Delay(900);
                await route.FallbackAsync();
            });

        var row = page.Locator("#pendingC2cList li").First;
        var confirmBtn = row.GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var rejectBtn = row.GetByRole(AriaRole.Button, new() { Name = "رد" });
        var confirmTask = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.EvaluateAsync("() => refreshPendingC2c()");
        await page.WaitForFunctionAsync(
            "() => { const li = document.querySelector('#pendingC2cList li'); if (!li) return false; const btns = li.querySelectorAll('button'); return btns.length >= 2 && [...btns].every(b => b.disabled); }");

        Assert.False(await confirmBtn.IsEnabledAsync());
        Assert.False(await rejectBtn.IsEnabledAsync());
        await rejectBtn.ClickAsync(new() { Force = true });
        await confirmBtn.ClickAsync(new() { Force = true });
        await confirmTask;
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingC2cList li')");
        Assert.Equal(1, confirmAttempts);
    }

    [Fact]
    public async Task pr37_fix_r6_wallet_pending_opposite_action_while_confirm_in_flight_sends_no_second_post()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r6wo", new { label = "o" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6wo", mobile = "0917" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(55_000, 0, "e2e-r6wo", "one"))).EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        var postCount = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/members/wallet/pending/", StringComparison.Ordinal)
                && (url.Contains("/confirm", StringComparison.Ordinal) || url.Contains("/reject", StringComparison.Ordinal)),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                postCount++;
                slowEntered.TrySetResult();
                await Task.Delay(900);
                await route.FallbackAsync();
            });

        var row = page.Locator("#pendingWalletList li").First;
        var confirmBtn = row.GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var rejectBtn = row.GetByRole(AriaRole.Button, new() { Name = "رد" });
        var confirmTask = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rejectBtn.ClickAsync(new() { Force = true });
        await confirmTask;
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingWalletList li')");
        Assert.Equal(1, postCount);
    }

    [Fact]
    public async Task pr37_fix_r6_c2c_pending_opposite_action_while_confirm_in_flight_sends_no_second_post()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-r6opp", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-R6OPP", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-R6OPP");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-R6OPP']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        var postCount = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync(
            url => url.Contains("/api/operator/payments/", StringComparison.Ordinal)
                && (url.Contains("/confirm", StringComparison.Ordinal) || url.Contains("/reject", StringComparison.Ordinal)),
            async route =>
            {
                if (!string.Equals(route.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    await route.FallbackAsync();
                    return;
                }

                postCount++;
                slowEntered.TrySetResult();
                await Task.Delay(900);
                await route.FallbackAsync();
            });

        var row = page.Locator("#pendingC2cList li").First;
        var confirmBtn = row.GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        var rejectBtn = row.GetByRole(AriaRole.Button, new() { Name = "رد" });
        var confirmTask = confirmBtn.ClickAsync();
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rejectBtn.ClickAsync(new() { Force = true });
        await confirmTask;
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingC2cList li')");
        Assert.Equal(1, postCount);
    }

    [Fact]
    public async Task pr37_fix_r6_wallet_pending_already_actioned_shows_persian_message_and_refreshes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var op = await LoginOperatorClientAsync();
        await S3TestHelpers.EnableShiftsAsync(mgr, true);
        await S3TestHelpers.OpenShiftAsync(op, 0);
        await mgr.PutAsJsonAsync("/api/operator/destination-cards/e2e-r6ad", new { label = "ad" });

        var tag = Guid.NewGuid().ToString("N")[..8];
        var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = "r6ad", mobile = "0918" + tag });
        created.EnsureSuccessStatusCode();
        var customerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(45_000, 0, "e2e-r6ad", "ad1"))).EnsureSuccessStatusCode();

        var page = await LoginAsync(op);
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");

        var pending = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/members/wallet/pending");
        Assert.NotNull(pending);
        var pendingId = pending![0].GetProperty("id").GetInt64();
        (await op.PostAsync($"/api/operator/members/wallet/pending/{pendingId}/confirm", null)).EnsureSuccessStatusCode();

        var confirmBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('قبلاً انجام شده')",
            null,
            new() { Timeout = 10_000 });
        var errAfterConfirm = await page.TextContentAsync("#error");
        Assert.Contains("این مورد قبلاً انجام شده است.", errAfterConfirm ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Top-up is not pending", errAfterConfirm ?? "", StringComparison.OrdinalIgnoreCase);
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingWalletList li')");
        await page.WaitForTimeoutAsync(500);
        var errAfterRefresh = await page.TextContentAsync("#error");
        Assert.Contains("این مورد قبلاً انجام شده است.", errAfterRefresh ?? "", StringComparison.Ordinal);

        (await op.PostAsJsonAsync($"/api/operator/members/customers/{customerId}/wallet/top-up",
            WalletTopUpTestJson.CardToCard(35_000, 0, "e2e-r6ad", "ad2"))).EnsureSuccessStatusCode();
        await page.EvaluateAsync("() => refreshPendingWalletTopups()");
        await page.WaitForSelectorAsync("#pendingWalletList li");
        pending = await op.GetFromJsonAsync<JsonElement[]>("/api/operator/members/wallet/pending");
        var rejectId = pending![0].GetProperty("id").GetInt64();
        (await op.PostAsync($"/api/operator/members/wallet/pending/{rejectId}/reject", null)).EnsureSuccessStatusCode();

        var rejectBtn = page.Locator("#pendingWalletList li").First
            .GetByRole(AriaRole.Button, new() { Name = "رد" });
        await rejectBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('قبلاً انجام شده')",
            null,
            new() { Timeout = 10_000 });
        var errAfterReject = await page.TextContentAsync("#error");
        Assert.Contains("این مورد قبلاً انجام شده است.", errAfterReject ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Top-up is not pending", errAfterReject ?? "", StringComparison.OrdinalIgnoreCase);
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingWalletList li')");
    }

    [Fact]
    public async Task pr37_fix_r6_c2c_pending_already_actioned_shows_persian_message_and_refreshes()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await LoginOperatorClientAsync("manager", "1234");
        await manager.PutAsJsonAsync("/api/operator/destination-cards/card-r6ad", new { label = "A" });
        await S3TestHelpers.RegisterPc(_api, "PC-R6AD", PilotSeed.PcNormalGroupId);
        await S3TestHelpers.RegisterPc(_api, "PC-R6AD2", PilotSeed.PcNormalGroupId);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PC-R6AD");
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await S3TestHelpers.CheckpointAsync(_api, sessionId, S3TestHelpers.T(2026, 6, 3, 15, 0));
        await _api.PostAsync($"/api/sessions/{sessionId}/stop", null);

        factory.Clock.SetTehran(2026, 6, 3, 14, 30);
        var sessionId2 = await S3TestHelpers.StartSession(_api, "PC-R6AD2");
        factory.Clock.SetTehran(2026, 6, 3, 15, 30);
        await S3TestHelpers.CheckpointAsync(_api, sessionId2, S3TestHelpers.T(2026, 6, 3, 15, 30));
        await _api.PostAsync($"/api/sessions/{sessionId2}/stop", null);

        var opClient = await LoginOperatorClientAsync();
        var page = await LoginAsync(opClient);

        await page.ClickAsync(".tile[data-station-id='PC-R6AD']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        long confirmLineId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            confirmLineId = (await db.PaymentLines.SingleAsync(p => p.ConfirmationStatus == PaymentConfirmationStatus.Pending)).Id;
        }

        (await opClient.PostAsync($"/api/operator/payments/{confirmLineId}/confirm", null)).EnsureSuccessStatusCode();

        var confirmBtn = page.Locator("#pendingC2cList li").First
            .GetByRole(AriaRole.Button, new() { Name = "تأیید" });
        await confirmBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('قبلاً انجام شده')",
            null,
            new() { Timeout = 10_000 });
        var errAfterConfirm = await page.TextContentAsync("#error");
        Assert.Contains("این مورد قبلاً انجام شده است.", errAfterConfirm ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Payment is not pending", errAfterConfirm ?? "", StringComparison.OrdinalIgnoreCase);
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingC2cList li')");

        await page.ClickAsync(".tile[data-station-id='PC-R6AD2']");
        await page.ClickAsync("#addC2cLineBtn");
        await FillC2cPaymentLineAsync(page);
        await page.ClickAsync("#confirmBtn");
        await page.WaitForSelectorAsync("#pendingC2cList li");

        long rejectLineId;
        using (var scope2 = factory.Services.CreateScope())
        {
            var db = scope2.ServiceProvider.GetRequiredService<AppDbContext>();
            rejectLineId = (await db.PaymentLines.SingleAsync(p => p.ConfirmationStatus == PaymentConfirmationStatus.Pending)).Id;
        }

        (await opClient.PostAsync($"/api/operator/payments/{rejectLineId}/reject", null)).EnsureSuccessStatusCode();

        var rejectBtn = page.Locator("#pendingC2cList li").First
            .GetByRole(AriaRole.Button, new() { Name = "رد" });
        await rejectBtn.ClickAsync();
        await page.WaitForFunctionAsync(
            "() => document.getElementById('error').textContent.includes('قبلاً انجام شده')",
            null,
            new() { Timeout = 10_000 });
        var errAfterReject = await page.TextContentAsync("#error");
        Assert.Contains("این مورد قبلاً انجام شده است.", errAfterReject ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("Payment is not pending", errAfterReject ?? "", StringComparison.OrdinalIgnoreCase);
        await page.WaitForFunctionAsync("() => !document.querySelector('#pendingC2cList li')");
    }

    [Fact]
    public async Task ss14_manager_login_renders_game_rate_inputs()
    {
        await S3TestHelpers.ResetAsync(factory);
        var mgr = await LoginOperatorClientAsync("manager", "1234");
        var page = await NewPanelPageAsync(mgr);
        await page.GotoAsync($"{PanelOrigin}/operator/index.html");
        await page.FillAsync("#username", "manager");
        await page.FillAsync("#pin", "1234");
        await page.ClickAsync("#loginBtn");
        await page.WaitForSelectorAsync("#grid .tile");
        await page.EvaluateAsync("async () => { await refreshCafeSettingsUi(); }");
        await page.WaitForSelectorAsync("#ss14GameRatesPanel:not(.hidden)");
        await page.WaitForSelectorAsync("#ss14GameRatesBody input[data-rate=\"hourly\"]");
        var count = await page.Locator("#ss14GameRatesBody input[data-rate=\"hourly\"]").CountAsync();
        Assert.True(count >= 5);
        AssertNoUnexpectedNativeDialogs(page);
    }

    [Fact]
    public async Task e2e_inpage_forms_native_dialog_guard_records_for_assert()
    {
        await S3TestHelpers.ResetAsync(factory);
        var page = await LoginAsync();
        AssertNoUnexpectedNativeDialogs(page);
    }
}
