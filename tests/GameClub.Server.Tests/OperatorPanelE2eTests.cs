using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
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
}
