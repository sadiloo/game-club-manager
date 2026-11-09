using System.Net.Http.Json;
using System.Text.Json;
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

    private async Task<IPage> NewPanelPageAsync(HttpClient? operatorClient = null)
    {
        var client = operatorClient ?? factory.CreateClient();
        var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
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

    private async Task<IPage> LoginAsync(HttpClient? operatorClient = null)
    {
        var page = await NewPanelPageAsync(operatorClient);
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
        var prompts = new Queue<string>(["80000", "", ""]);
        page.Dialog += async (_, d) =>
        {
            var v = prompts.Count > 0 ? prompts.Dequeue() : "";
            await d.AcceptAsync(v);
        };

        await page.ClickAsync(".tile[data-station-id='PC-E2E']");
        await page.ClickAsync("#addC2cLineBtn");
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
        var detail = await page.TextContentAsync("#billDetail");
        Assert.Contains("120000", detail!.Replace(",", "").Replace(" ", ""));

        page.Dialog += async (_, d) => await d.AcceptAsync(null);
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
        var detailBefore = await page.TextContentAsync("#billDetail");
        Assert.Contains("بوفه15000", detailBefore!.Replace(" ", ""));

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
        var before = await page.TextContentAsync("#billDetail");
        Assert.Contains("بوفه15000", before!.Replace(" ", ""));

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

        var after = await page.TextContentAsync("#billDetail");
        var afterNorm = after!.Replace(" ", "");
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
        var before = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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

        var after = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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
        var sessionBillBefore = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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
        var buffetBeforeRemove = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
        Assert.Contains("=30000", buffetBeforeRemove);

        await page.Locator("#buffetSaleLines button", new() { HasText = "حذف" }).First.ClickAsync();
        await page.WaitForResponseAsync(r =>
            r.Request.Method == "GET"
            && r.Url.Contains("/buffet-only/", StringComparison.Ordinal)
            && r.Ok);
        var buffetAfterRemove = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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
        var detail = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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
        var billDetail = (await page.TextContentAsync("#billDetail"))!.Replace(" ", "");
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
        page.Dialog += (_, d) => d.AcceptAsync("STALE-C");
        await page.ClickAsync("#transferBtn");
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
        page.Dialog += (_, d) => d.AcceptAsync("PS5-TO");
        await page.ClickAsync(".tile[data-station-id='PS5-FROM']");
        await page.WaitForSelectorAsync("#transferBtn:not(.hidden)");
        var transferTask = page.WaitForResponseAsync(r =>
            r.Url.Contains("/transfer", StringComparison.Ordinal) && r.Request.Method == "POST");
        await page.ClickAsync("#transferBtn");
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
}
