using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Server.Data;
using GameClub.Server.Operator;
using GameClub.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Cu15QuickCustomerAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    private static async Task<JsonElement> CreateCustomerAsync(
        HttpClient op,
        string name,
        string mobile,
        string? membershipCode = null,
        Guid? linkSessionId = null,
        Guid? linkOpenSaleId = null)
    {
        var response = await op.PostAsJsonAsync("/api/operator/customers", new
        {
            name,
            mobile,
            membershipCode,
            linkSessionId,
            linkOpenSaleId,
        });
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task cu15_scenario_01_create_from_bill_links_without_changing_totals()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var before = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        await CreateCustomerAsync(op, "علی", "09121234567", linkSessionId: sessionId);
        var after = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        S3TestHelpers.AssertMoney(before, 110_000, 110_000);
        S3TestHelpers.AssertMoney(after, 110_000, 110_000);
        Assert.Equal("علی", after.GetProperty("linkedCustomer").GetProperty("name").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.Customers.Where(c => c.Mobile == "09121234567").ToListAsync());
    }

    [Fact]
    public async Task cu15_scenario_02_create_from_station_without_opening_bill()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await CreateCustomerAsync(op, "زهرا", "09120000000", linkSessionId: sessionId);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync()).Status);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal("زهرا", bill.GetProperty("linkedCustomer").GetProperty("name").GetString());
    }

    [Fact]
    public async Task cu15_scenario_03_duplicate_mobile_persian_digits_returns_409()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await CreateCustomerAsync(op, "علی", "09121234567");
        var dup = await op.PostAsJsonAsync("/api/operator/customers", new { name = "دیگر", mobile = "۰۹۱۲۱۲۳۴۵۶۷" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ali = await db.Customers.SingleAsync();
        Assert.Equal("علی", ali.Name);
        Assert.Equal("09121234567", ali.Mobile);
    }

    [Fact]
    public async Task cu15_scenario_04_search_by_mobile_finds_customer()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await CreateCustomerAsync(op, "علی", "09121234567");
        var hits = await op.GetFromJsonAsync<JsonElement>("/api/operator/customers/search?q=09121234567");
        Assert.Single(hits.EnumerateArray());
        var persianQuery = Uri.EscapeDataString("۰۹۱۲۱۲۳۴۵۶۷");
        var persian = await op.GetFromJsonAsync<JsonElement>($"/api/operator/customers/search?q={persianQuery}");
        Assert.Contains(persian.EnumerateArray(), h => h.GetProperty("name").GetString() == "علی");
    }

    [Fact]
    public async Task cu15_scenario_05_search_by_name_finds_customer()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await CreateCustomerAsync(op, "علی", "09121234567");
        var hits = await op.GetFromJsonAsync<JsonElement>("/api/operator/customers/search?q=%D8%B9%D9%84%DB%8C");
        Assert.Contains(hits.EnumerateArray(), h => h.GetProperty("mobile").GetString() == "09121234567");
    }

    [Fact]
    public async Task cu15_scenario_06_search_by_membership_code_finds_only_matching_customer()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await CreateCustomerAsync(op, "علی", "09121234567");
        await CreateCustomerAsync(op, "رضا", "09121111111", membershipCode: "A-1001");
        var hits = await op.GetFromJsonAsync<JsonElement>("/api/operator/customers/search?q=A-1001");
        Assert.Single(hits.EnumerateArray());
        Assert.Equal("رضا", hits.EnumerateArray().First().GetProperty("name").GetString());
    }

    [Fact]
    public async Task cu15_scenario_07_duplicate_membership_code_returns_409()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await CreateCustomerAsync(op, "رضا", "09121111111", membershipCode: "A-1001");
        var dup = await op.PostAsJsonAsync("/api/operator/customers", new { name = "دیگر", mobile = "09122222222", membershipCode = "a-1001" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.CountAsync());
    }

    [Fact]
    public async Task cu15_scenario_08_create_without_membership_code_succeeds()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var created = await CreateCustomerAsync(op, "سارا", "09123334444");
        Assert.True(created.GetProperty("id").GetGuid() != Guid.Empty);
        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.SingleAsync();
        Assert.Null(row.MembershipCode);
    }

    [Fact]
    public async Task cu15_scenario_09_link_on_playing_session_confirm_stores_customer_same_money()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var baselineId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{baselineId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, baselineId, 110_000);
        using (var scope = factory.Services.CreateScope())
        {
            var baseline = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.AsNoTracking().SingleAsync();
            Assert.Null(baseline.CustomerId);
            Assert.Equal(110_000, baseline.CollectibleToman);
        }

        await S3TestHelpers.ResetAsync(factory);
        op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        var customer = await CreateCustomerAsync(op, "علی", "09121234567");
        var link = await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId = customer.GetProperty("id").GetGuid() });
        link.EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            Assert.Equal(SessionStatus.Open, (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Sessions.SingleAsync()).Status);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var afterScope = factory.Services.CreateScope();
        var db = afterScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Customer).SingleAsync();
        Assert.Equal("علی", invoice.Customer!.Name);
        Assert.Equal(110_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task cu15_scenario_10_no_account_play_unchanged()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var session = await db.Sessions.AsNoTracking().SingleAsync();
        Assert.Null(session.CustomerId);
        var invoice = await db.Invoices.AsNoTracking().SingleAsync();
        Assert.Null(invoice.CustomerId);
        Assert.Equal(110_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task cu15_scenario_11_replace_before_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var ali = await CreateCustomerAsync(op, "علی", "09121234567");
        var zahra = await CreateCustomerAsync(op, "زهرا", "09120000000");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId = ali.GetProperty("id").GetGuid() });
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId = zahra.GetProperty("id").GetGuid() });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 110_000);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.Customers.CountAsync());
        var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync();
        Assert.Equal("زهرا", invoice.Customer!.Name);
    }

    [Fact]
    public async Task cu15_scenario_12_buffet_only_stores_customer_on_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new { catalogItemId = CatalogSeed.SodaId });
        await CreateCustomerAsync(op, "رضا", "09121111111", linkOpenSaleId: openSaleId);
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        var detail = await op.GetFromJsonAsync<JsonElement>($"/api/operator/buffet-only/{openSaleId}");
        S3TestHelpers.AssertMoney(detail.GetProperty("bill"), 15_000, 15_000);
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new
        {
            payments = new[] { new { method = "Cash", amountToman = 15_000L } },
        });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.Include(i => i.Customer).SingleAsync();
        Assert.Equal("رضا", invoice.Customer!.Name);
        Assert.Equal(15_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task cu15_scenario_13_operator_cannot_edit_customer_profile()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var created = await CreateCustomerAsync(op, "علی", "09121234567");
        var response = await op.PutAsJsonAsync($"/api/operator/customers/{created.GetProperty("id").GetGuid()}", new { name = "hack" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task cu15_scenario_14_audit_create_link_unlink()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var customer = await CreateCustomerAsync(op, "علی", "09121234567");
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId = customer.GetProperty("id").GetGuid() });
        await op.PutAsJsonAsync($"/api/operator/sessions/{sessionId}/customer", new { customerId = (Guid?)null });
        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains(actions, a => a == "customer.create");
        Assert.Contains(actions, a => a == "customer.link");
        Assert.Contains(actions, a => a == "customer.unlink");
    }

    [Fact]
    public async Task cu15_scenario_15_no_cap_on_customer_count()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        for (var i = 0; i < 901; i++)
        {
            var mobile = $"0912{i:D7}";
            var created = await op.PostAsJsonAsync("/api/operator/customers", new { name = $"c{i}", mobile });
            created.EnsureSuccessStatusCode();
        }

        using var scope = factory.Services.CreateScope();
        Assert.Equal(901, await scope.ServiceProvider.GetRequiredService<AppDbContext>().Customers.CountAsync());
    }
}
