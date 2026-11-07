using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class S3bBuffetAcceptanceTests(GameClubWebApplicationFactory factory)
{
    private readonly HttpClient _api = factory.CreateClientWithKey();

    [Fact]
    public async Task s3b_scenario_01_tile_add_during_play()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(9, await db.CatalogItems.Where(i => i.Id == CatalogSeed.SodaId).Select(i => i.StockQuantity).SingleAsync());
            Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync()).Status);
        }

        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        var bill = await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        var body = await bill.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(110_000, body.GetProperty("timeGrossToman").GetInt64());
        Assert.Equal(15_000, body.GetProperty("buffetGrossToman").GetInt64());
        S3TestHelpers.AssertMoney(body, 125_000, 125_000);
    }

    [Fact]
    public async Task s3b_scenario_02_unstocked_item()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.TeaId });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tea = await db.CatalogItems.SingleAsync(i => i.Id == CatalogSeed.TeaId);
        Assert.Null(tea.StockQuantity);
        Assert.Single(await db.OpenSaleLines.Where(l => l.CatalogItemId == CatalogSeed.TeaId).ToListAsync());
    }

    [Fact]
    public async Task s3b_scenario_03_repeat_click_merges_quantity()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        for (var i = 0; i < 3; i++)
        {
            await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.ChipsId });
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var line = await db.OpenSaleLines.SingleAsync(l => l.CatalogItemId == CatalogSeed.ChipsId);
        Assert.Equal(3, line.Quantity);
        Assert.Equal(60_000, line.LineTotalToman);
        Assert.Equal(17, await db.CatalogItems.Where(i => i.Id == CatalogSeed.ChipsId).Select(i => i.StockQuantity).SingleAsync());
    }

    [Fact]
    public async Task s3b_scenario_04_insufficient_stock_returns_409()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        await manager.PutAsJsonAsync($"/api/operator/catalog/items/low-stock-test", new
        {
            categoryId = CatalogSeed.FoodCategoryId,
            name = "تست",
            kind = "Stocked",
            unitPriceToman = 1_000,
            stockQuantity = 1,
            lowStockWarningThreshold = 0,
        });
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = "low-stock-test" })).EnsureSuccessStatusCode();
        var second = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = "low-stock-test" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.CatalogItems.Where(i => i.Id == "low-stock-test").Select(i => i.StockQuantity).SingleAsync());
        Assert.Single(await db.OpenSaleLines.Where(l => l.CatalogItemId == "low-stock-test").ToListAsync());
    }

    [Fact]
    public async Task s3b_scenario_05_low_stock_warning_read()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        for (var i = 0; i < 6; i++)
        {
            await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        }

        var warnings = await op.GetFromJsonAsync<JsonElement>("/api/operator/catalog/low-stock");
        Assert.Contains(warnings.EnumerateArray(), w => w.GetProperty("id").GetString() == CatalogSeed.SodaId);
    }

    [Fact]
    public async Task s3b_scenario_06_fixed_special_service()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        var sale = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.DamageFineId });
        var body = await sale.Content.ReadFromJsonAsync<JsonElement>();
        var line = body.GetProperty("lines").EnumerateArray().Single();
        Assert.Equal("Service", line.GetProperty("kind").GetString());
        Assert.Equal(50_000, line.GetProperty("lineTotalToman").GetInt64());
        Assert.Equal(50_000, body.GetProperty("servicesGrossToman").GetInt64());
    }

    [Fact]
    public async Task s3b_scenario_07_custom_amount_service()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        var sale = await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.CustomServiceId, customAmountToman = 20_000 });
        var line = (await sale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines").EnumerateArray().Single();
        Assert.Equal(20_000, line.GetProperty("lineTotalToman").GetInt64());
    }

    [Fact]
    public async Task s3b_scenario_08_buffet_only_confirm()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var start = await op.PostAsync("/api/operator/buffet-only/start", null);
        var openSaleId = (await start.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("openSaleId").GetGuid();
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new { catalogItemId = CatalogSeed.ChipsId });
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/lines", new { catalogItemId = CatalogSeed.ChipsId });
        await op.PostAsync($"/api/operator/buffet-only/{openSaleId}/checkout", null);
        await op.PostAsJsonAsync($"/api/operator/buffet-only/{openSaleId}/confirm", new { payments = new[] { new { method = "Cash", amountToman = 40_000 } } });
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invoice = await db.Invoices.SingleAsync();
        Assert.Equal(0, invoice.TimeGrossToman);
        Assert.Equal(40_000, invoice.BuffetGrossToman);
        Assert.Equal(40_000, invoice.CollectibleToman);
        Assert.Null(invoice.SessionId);
        Assert.Equal(18, await db.CatalogItems.Where(i => i.Id == CatalogSeed.ChipsId).Select(i => i.StockQuantity).SingleAsync());
    }

    [Fact]
    public async Task s3b_scenario_09_remove_before_confirm_restores_stock()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        var addBody = await (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var lineId = addBody.GetProperty("lines").EnumerateArray().Single().GetProperty("id").GetInt64();
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.DeleteAsync($"/api/operator/sessions/{sessionId}/sale/lines/{lineId}");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(10, await db.CatalogItems.Where(i => i.Id == CatalogSeed.SodaId).Select(i => i.StockQuantity).SingleAsync());
        var bill = await op.GetFromJsonAsync<JsonElement>($"/api/operator/sessions/{sessionId}/bill");
        Assert.Equal(110_000, bill.GetProperty("timeGrossToman").GetInt64());
        Assert.Equal(0, bill.GetProperty("buffetGrossToman").GetInt64());
        Assert.Contains(await db.AuditLog.Select(a => a.Action).ToListAsync(), a => a == "sale.line_remove");
    }

    [Fact]
    public async Task s3b_scenario_10_cancel_bill_keeps_sale_lines()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/cancel", null);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(SessionStatus.Open, (await db.Sessions.SingleAsync()).Status);
        Assert.Single(await db.OpenSaleLines.Where(l => l.CatalogItemId == CatalogSeed.SodaId).ToListAsync());
    }

    [Fact]
    public async Task s3b_scenario_11_confirm_conservation_125000()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 0);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        factory.Clock.SetTehran(2026, 6, 3, 14, 10);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        factory.Clock.SetTehran(2026, 6, 3, 15, 0);
        await op.PostAsync($"/api/operator/sessions/{sessionId}/bill/open", null);
        await S3TestHelpers.ConfirmCashAsync(op, sessionId, 125_000);
        using var scope = factory.Services.CreateScope();
        var invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Invoices.SingleAsync();
        Assert.Equal(110_000, invoice.TimeGrossToman);
        Assert.Equal(15_000, invoice.BuffetGrossToman);
        Assert.Equal(125_000, invoice.CollectibleToman);
    }

    [Fact]
    public async Task s3b_scenario_12_concurrent_last_unit()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op1 = await factory.CreateOperatorClientAsync("operator", "1111");
        var op2 = await factory.CreateOperatorClientAsync("manager", "1234");
        var manager = op2;
        await manager.PutAsJsonAsync($"/api/operator/catalog/items/race-item", new
        {
            categoryId = CatalogSeed.FoodCategoryId,
            name = "رقابت",
            kind = "Stocked",
            unitPriceToman = 1_000,
            stockQuantity = 1,
            lowStockWarningThreshold = 0,
        });
        await S3TestHelpers.RegisterConsole(_api, op1, "PS5-1", 1);
        var s1 = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await S3TestHelpers.RegisterConsole(_api, op2, "PS5-2", 1);
        var s2 = await S3TestHelpers.StartSession(_api, "PS5-2", 1);
        var responses = await Task.WhenAll(
            op1.PostAsJsonAsync($"/api/operator/sessions/{s1}/sale/lines", new { catalogItemId = "race-item" }),
            op2.PostAsJsonAsync($"/api/operator/sessions/{s2}/sale/lines", new { catalogItemId = "race-item" }));
        var codes = responses.Select(r => (int)r.StatusCode).ToArray();
        Assert.Contains(409, codes);
        Assert.Contains(codes, c => c is >= 200 and < 300);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(0, await db.CatalogItems.Where(i => i.Id == "race-item").Select(i => i.StockQuantity).SingleAsync());
        Assert.Single(await db.OpenSaleLines.Where(l => l.CatalogItemId == "race-item").ToListAsync());
    }

    [Fact]
    public async Task s3b_scenario_13_no_line_cap()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        for (var i = 0; i < 16; i++)
        {
            await manager.PutAsJsonAsync($"/api/operator/catalog/items/extra-{i}", new
            {
                categoryId = CatalogSeed.FoodCategoryId,
                name = $"قلم {i}",
                kind = "Unstocked",
                unitPriceToman = 1_000,
                stockQuantity = (int?)null,
                lowStockWarningThreshold = (int?)null,
            });
        }

        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        for (var i = 0; i < 16; i++)
        {
            (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = $"extra-{i}" })).EnsureSuccessStatusCode();
        }

        using var scope = factory.Services.CreateScope();
        Assert.Equal(16, await scope.ServiceProvider.GetRequiredService<AppDbContext>().OpenSaleLines.CountAsync());
    }

    [Fact]
    public async Task s3b_scenario_14_audit_rows_on_catalog_and_sale()
    {
        await S3TestHelpers.ResetAsync(factory);
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var op = await factory.CreateOperatorClientAsync();
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId });
        await manager.PostAsJsonAsync($"/api/operator/catalog/items/{CatalogSeed.SodaId}/stock-adjust", new { delta = 2, reason = "restock" });
        using var scope = factory.Services.CreateScope();
        var actions = await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLog.Select(a => a.Action).ToListAsync();
        Assert.Contains("sale.line_add", actions);
        Assert.Contains("catalog.stock_adjust", actions);
    }

    [Fact]
    public async Task s3b_scenario_15_catalog_forbidden_for_operator()
    {
        await S3TestHelpers.ResetAsync(factory);
        var op = await factory.CreateOperatorClientAsync();
        var manager = await factory.CreateOperatorClientAsync("manager", "1234");
        var put = await op.PutAsJsonAsync("/api/operator/catalog/items/op-item", new
        {
            categoryId = CatalogSeed.FoodCategoryId,
            name = "x",
            kind = "Unstocked",
            unitPriceToman = 1,
            stockQuantity = (int?)null,
            lowStockWarningThreshold = (int?)null,
        });
        Assert.Equal(HttpStatusCode.Forbidden, put.StatusCode);
        var adjust = await op.PostAsJsonAsync($"/api/operator/catalog/items/{CatalogSeed.SodaId}/stock-adjust", new { delta = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, adjust.StatusCode);
        await S3TestHelpers.RegisterConsole(_api, op, "PS5-1", 1);
        var sessionId = await S3TestHelpers.StartSession(_api, "PS5-1", 1);
        (await op.PostAsJsonAsync($"/api/operator/sessions/{sessionId}/sale/lines", new { catalogItemId = CatalogSeed.SodaId })).EnsureSuccessStatusCode();
        (await manager.PutAsJsonAsync("/api/operator/catalog/items/mgr-item", new
        {
            categoryId = CatalogSeed.FoodCategoryId,
            name = "y",
            kind = "Stocked",
            unitPriceToman = 1,
            stockQuantity = 1,
            lowStockWarningThreshold = 0,
        })).EnsureSuccessStatusCode();
    }
}
