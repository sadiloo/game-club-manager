using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;

namespace GameClub.Server.Tests;

/// <summary>
/// jsdom cannot compute scrollWidth/clientWidth (always 0); Chromium layout assertions live here.
/// </summary>
[Collection("server")]
public sealed class InpageFormsOperatorNarrowViewportLayoutTests(GameClubWebApplicationFactory factory) : IAsyncLifetime
{
    private const string PanelOrigin = "http://panel.test";
    private const int ViewportWidth = 390;
    private const string LatinLong = "CustomerNameWithNoSpacesOrBreaksAtAllJustEightyFiveLatinCharactersxxxxxxxxxxx";
    private const string PersianLong = "مشتریبدونفاصلهبسیارطولانیبرایتستشکستخطدرعرض۳۹۰پیکسلبا۸۵کاراکتر";

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

  [Theory]
    [InlineData("latin", LatinLong)]
    [InlineData("persian", PersianLong)]
    public async Task narrow_viewport_long_names_do_not_widen_document(string variant, string longName)
    {
        await S3TestHelpers.ResetAsync(factory);
        var client = factory.CreateClientWithKey();
        await LoginOperatorClientAsync(client);

        var context = await _browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = 844 },
        });
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/*", route => RouteViaClientAsync(client, route));

        await page.GotoAsync($"{PanelOrigin}/operator/index.html");
        await page.FillAsync("#username", "operator");
        await page.FillAsync("#pin", "1111");
        await page.ClickAsync("#loginBtn");
        await page.WaitForSelectorAsync("#grid");

        await page.EvaluateAsync(
            @"(name) => {
  const chip = document.getElementById('customerChip');
  chip.classList.remove('hidden');
  chip.textContent = `پیوسته: ${name} — 09121234567`;
  const box = document.getElementById('customerSearchResults');
  box.replaceChildren();
  const btn = document.createElement('button');
  btn.className = 'customer-hit';
  btn.type = 'button';
  btn.textContent = `${name} — 09121234567`;
  box.appendChild(btn);
  document.getElementById('sessionLabel').textContent = `${name} — Playing`;
  document.getElementById('walletBalance').textContent = `کیف پول: ${name}`;
  document.getElementById('buffetContext').textContent = `ایستگاه ${name} — جلسه فعال`;
  document.getElementById('sessionActions').classList.remove('hidden');
  document.getElementById('walletActions').classList.remove('hidden');
  const saleHost = document.getElementById('customerPanel');
  const line = document.createElement('div');
  line.className = 'sale-line';
  const label = document.createElement('span');
  label.textContent = `${name} ×1 = 120000`;
  const del = document.createElement('button');
  del.type = 'button';
  del.textContent = 'حذف';
  line.append(label, del);
  saleHost.appendChild(line);
}",
            longName);

        await AssertNoHorizontalOverflowAsync(page, $"{variant}-panel-in-flow");

        await page.EvaluateAsync(
            @"(name) => {
  void cpConfirm({
    title: name,
    body: `${name}. فقط یک بار برای هر عضو ثبت می‌شود.`,
    confirmLabel: 'باشه',
  });
}",
            longName);

        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");

        await AssertNoHorizontalOverflowAsync(page, $"{variant}-panel-and-confirm");

        await page.ClickAsync("#cpLayer [data-cp-role='close']");
        await page.Locator("#cpLayer").WaitForAsync(new() { State = WaitForSelectorState.Hidden });

        await page.EvaluateAsync(
            @"(name) => {
  cpPick({
    title: 'انتخاب',
    items: [{ id: '1', label: `${name} — 09121234567` }],
    confirmLabel: 'انتخاب',
  });
}",
            longName);

        await page.WaitForSelectorAsync("#cpLayer:not(.hidden)");
        await AssertNoHorizontalOverflowAsync(page, $"{variant}-pick");

        await context.CloseAsync();
    }

    private static async Task AssertNoHorizontalOverflowAsync(IPage page, string scene)
    {
        var metrics = await page.EvaluateAsync<JsonElement>(
            @"() => ({
    html: { scrollWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth },
    body: { scrollWidth: document.body.scrollWidth, clientWidth: document.body.clientWidth },
  })");

        var htmlSw = metrics.GetProperty("html").GetProperty("scrollWidth").GetInt32();
        var htmlCw = metrics.GetProperty("html").GetProperty("clientWidth").GetInt32();
        var bodySw = metrics.GetProperty("body").GetProperty("scrollWidth").GetInt32();
        var bodyCw = metrics.GetProperty("body").GetProperty("clientWidth").GetInt32();

        Assert.True(htmlSw <= htmlCw, $"{scene}: html scrollWidth {htmlSw} > clientWidth {htmlCw}");
        Assert.True(bodySw <= bodyCw, $"{scene}: body scrollWidth {bodySw} > clientWidth {bodyCw}");
    }

    private static async Task RouteViaClientAsync(HttpClient client, IRoute route)
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
    }

    private async Task<HttpClient> LoginOperatorClientAsync(HttpClient client)
    {
        var login = await client.PostAsJsonAsync("/api/operator/auth/login", new { username = "operator", pin = "1111" });
        login.EnsureSuccessStatusCode();
        return client;
    }
}