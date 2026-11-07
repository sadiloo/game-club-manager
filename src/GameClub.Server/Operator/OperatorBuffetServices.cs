using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed record SaleLineDto(
    long Id,
    string CatalogItemId,
    string Name,
    string Kind,
    int Quantity,
    long UnitPriceToman,
    long LineTotalToman);

public static class OpenSaleMapper
{
    public static IReadOnlyList<BuffetLine> ToBuffetLines(IEnumerable<OpenSaleLineEntity> lines)
        => lines.Where(l => l.Kind == SaleLineKind.Buffet).Select(l =>
            new BuffetLine(l.CatalogItemId, l.CatalogItem?.Name ?? l.CatalogItemId, l.Quantity, l.UnitPriceToman)).ToList();

    public static IReadOnlyList<ServiceLine> ToServiceLines(IEnumerable<OpenSaleLineEntity> lines)
        => lines.Where(l => l.Kind == SaleLineKind.Service).Select(l =>
            new ServiceLine(l.CatalogItemId, l.CatalogItem?.Name ?? l.CatalogItemId, l.LineTotalToman)).ToList();

    public static IReadOnlyList<SaleLineDto> ToDtos(IEnumerable<OpenSaleLineEntity> lines)
        => lines.OrderBy(l => l.Id).Select(l => new SaleLineDto(
            l.Id,
            l.CatalogItemId,
            l.CatalogItem?.Name ?? l.CatalogItemId,
            l.Kind.ToString(),
            l.Quantity,
            l.UnitPriceToman,
            l.LineTotalToman)).ToList();
}

public sealed class CatalogSeed
{
    public const string DrinksCategoryId = "drinks";
    public const string FoodCategoryId = "food";
    public const string ServicesCategoryId = "services";

    public const string SodaId = "soda";
    public const string ChipsId = "chips";
    public const string TeaId = "tea";
    public const string DamageFineId = "damage-fine";
    public const string CustomServiceId = "custom-service";

    public static void Ensure(AppDbContext db)
    {
        if (db.CatalogCategories.Any())
        {
            return;
        }

        db.CatalogCategories.AddRange(
            new CatalogCategoryEntity { Id = DrinksCategoryId, Name = "نوشیدنی", SortOrder = 1 },
            new CatalogCategoryEntity { Id = FoodCategoryId, Name = "خوراکی", SortOrder = 2 },
            new CatalogCategoryEntity { Id = ServicesCategoryId, Name = "خدمات", SortOrder = 3 });
        db.CatalogItems.AddRange(
            new CatalogItemEntity
            {
                Id = SodaId,
                CategoryId = DrinksCategoryId,
                Name = "نوشابه",
                Kind = CatalogProductKind.Stocked,
                UnitPriceToman = 15_000,
                StockQuantity = 10,
                LowStockWarningThreshold = 5,
            },
            new CatalogItemEntity
            {
                Id = TeaId,
                CategoryId = DrinksCategoryId,
                Name = "چای",
                Kind = CatalogProductKind.Unstocked,
                UnitPriceToman = 10_000,
            },
            new CatalogItemEntity
            {
                Id = ChipsId,
                CategoryId = FoodCategoryId,
                Name = "چیپس",
                Kind = CatalogProductKind.Stocked,
                UnitPriceToman = 20_000,
                StockQuantity = 20,
                LowStockWarningThreshold = 5,
            },
            new CatalogItemEntity
            {
                Id = DamageFineId,
                CategoryId = ServicesCategoryId,
                Name = "جریمه خسارت دسته",
                Kind = CatalogProductKind.ServiceFixed,
                UnitPriceToman = 50_000,
            },
            new CatalogItemEntity
            {
                Id = CustomServiceId,
                CategoryId = ServicesCategoryId,
                Name = "دلخواه",
                Kind = CatalogProductKind.ServiceCustom,
                UnitPriceToman = 0,
            });
    }
}

public sealed class OperatorCatalogService(AppDbContext db, AuditService audit, OperatorContext context)
{
    public async Task<IReadOnlyList<object>> ListCatalogAsync(CancellationToken ct)
    {
        var categories = await db.CatalogCategories.AsNoTracking()
            .Include(c => c.Items.Where(i => i.IsActive))
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);
        return categories.Select(c => new
        {
            c.Id,
            c.Name,
            items = c.Items.OrderBy(i => i.Name).Select(i => new
            {
                i.Id,
                i.Name,
                kind = i.Kind.ToString(),
                i.UnitPriceToman,
                stockQuantity = i.StockQuantity,
                lowStockWarningThreshold = i.LowStockWarningThreshold,
                lowStockWarning = i.StockQuantity is int q && i.LowStockWarningThreshold is int t && q <= t,
            }),
        }).Cast<object>().ToList();
    }

    public async Task<IReadOnlyList<object>> LowStockAsync(CancellationToken ct)
    {
        var rows = await db.CatalogItems.AsNoTracking()
            .Where(i => i.IsActive && i.Kind == CatalogProductKind.Stocked && i.StockQuantity != null && i.LowStockWarningThreshold != null
                && i.StockQuantity <= i.LowStockWarningThreshold)
            .OrderBy(i => i.Name)
            .Select(i => new { i.Id, i.Name, stockQuantity = i.StockQuantity, lowStockWarningThreshold = i.LowStockWarningThreshold })
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    public void EnsureMayEditCatalog()
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Catalog edit is manager-only.");
        }
    }

    public sealed record CatalogItemInput(
        string Id,
        string CategoryId,
        string Name,
        string Kind,
        long UnitPriceToman,
        int? StockQuantity,
        int? LowStockWarningThreshold);

    public async Task<CatalogItemEntity> UpsertItemAsync(CatalogItemInput input, CancellationToken ct)
    {
        EnsureMayEditCatalog();
        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_catalog", "Item id and name are required.");
        }

        if (!Enum.TryParse<CatalogProductKind>(input.Kind, true, out var kind))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_catalog", "Unknown product kind.");
        }

        ValidateKind(kind, input.StockQuantity, input.LowStockWarningThreshold);
        if (input.UnitPriceToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_money", "Unit price cannot be negative.");
        }

        var category = await db.CatalogCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == input.CategoryId, ct)
            ?? throw new ApiException(StatusCodes.Status400BadRequest, "unknown_category", "Category not found.");
        var existing = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == input.Id, ct);
        var before = existing is null
            ? null
            : new { existing.Name, kind = existing.Kind.ToString(), existing.UnitPriceToman, existing.StockQuantity, existing.LowStockWarningThreshold };
        if (existing is null)
        {
            existing = new CatalogItemEntity
            {
                Id = input.Id.Trim(),
                CategoryId = category.Id,
            };
            db.CatalogItems.Add(existing);
        }

        existing.Name = input.Name.Trim();
        existing.Kind = kind;
        existing.UnitPriceToman = input.UnitPriceToman;
        existing.StockQuantity = kind == CatalogProductKind.Stocked ? input.StockQuantity : null;
        existing.LowStockWarningThreshold = kind == CatalogProductKind.Stocked ? input.LowStockWarningThreshold : null;
        existing.IsActive = true;
        await audit.AppendAsync("catalog.item_upsert", new { input.Id, before, after = input }, ct);
        await db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task AdjustStockAsync(string itemId, int delta, string? reason, CancellationToken ct)
    {
        EnsureMayEditCatalog();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("""SELECT "Id" FROM catalog_items WHERE "Id" = {0} FOR UPDATE""", itemId);
        }
        catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "catalog_busy", "Catalog item is locked.");
        }

        var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == itemId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Catalog item not found.");
        if (item.Kind != CatalogProductKind.Stocked)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "not_stocked", "Only stocked items have inventory.");
        }

        var before = item.StockQuantity;
        var after = (before ?? 0) + delta;
        if (after < 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "insufficient_stock", "Stock cannot go negative.");
        }

        item.StockQuantity = after;
        await audit.AppendAsync("catalog.stock_adjust", new { itemId, delta, reason, before, after }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void ValidateKind(CatalogProductKind kind, int? stock, int? warning)
    {
        if (kind == CatalogProductKind.Stocked)
        {
            if (stock is null or < 0 || warning is null or < 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_catalog", "Stocked items need stock and warning threshold.");
            }
        }
        else if (kind == CatalogProductKind.Unstocked && stock is not null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_catalog", "Unstocked items do not store stock quantity.");
        }
    }
}

public sealed class OperatorBuffetSaleService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    BillCalculator calculator,
    SessionCloser closer,
    OperatorBillService bills,
    OperatorPolicies policies)
{
    public async Task<OpenSaleEntity> GetOrCreateForSessionAsync(PlaySessionEntity session, CancellationToken ct)
    {
        var sale = await db.OpenSales.Include(o => o.Lines).FirstOrDefaultAsync(o => o.SessionId == session.Id, ct);
        if (sale is not null)
        {
            return sale;
        }

        sale = new OpenSaleEntity
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            StationId = session.StationId,
            IsBuffetOnly = false,
        };
        db.OpenSales.Add(sale);
        await db.SaveChangesAsync(ct);
        return sale;
    }

    public async Task<object> GetSessionSaleAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        var sale = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .FirstOrDefaultAsync(o => o.SessionId == sessionId, ct);
        if (sale is null)
        {
            return new
            {
                sessionId,
                lines = Array.Empty<SaleLineDto>(),
                timeGrossToman = 0L,
                buffetGrossToman = 0L,
                servicesGrossToman = 0L,
                linkedCustomer = CustomerMapper.ToLinkedChip(session?.Customer),
            };
        }

        return MapSale(sale, session?.Customer);
    }

    public async Task<object> AddLineToSessionAsync(Guid sessionId, string catalogItemId, long? customAmountToman, CancellationToken ct)
    {
        return await WithSessionSaleLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Status is SessionStatus.Closed)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "closed", "Session is closed.");
            }

            var sale = await GetOrCreateForSessionAsync(session, innerCt);
            await AddLineCoreAsync(sale, catalogItemId, customAmountToman, innerCt);
            await RefreshSessionPendingBillIfAnyAsync(session, sale, innerCt);
            await audit.AppendAsync("sale.line_add", new { sessionId, catalogItemId, customAmountToman }, innerCt);
            return await MapSaleWithCustomerAsync(sale, innerCt);
        }, ct);
    }

    public async Task<object> RemoveLineFromSessionAsync(Guid sessionId, long lineId, CancellationToken ct)
    {
        return await WithSessionSaleLockAsync(sessionId, async (session, innerCt) =>
        {
            var sale = await db.OpenSales.Include(o => o.Lines).FirstOrDefaultAsync(o => o.SessionId == session.Id, innerCt)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "no_sale", "No open sale for session.");
            var line = sale.Lines.FirstOrDefault(l => l.Id == lineId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Sale line not found.");
            await RemoveLineCoreAsync(line, innerCt);
            await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(innerCt);
            await RefreshSessionPendingBillIfAnyAsync(session, sale, innerCt);
            await audit.AppendAsync("sale.line_remove", new { sessionId, lineId }, innerCt);
            return await MapSaleWithCustomerAsync(sale, innerCt);
        }, ct);
    }

    public async Task<OpenSaleEntity> StartBuffetOnlyAsync(CancellationToken ct)
    {
        var sale = new OpenSaleEntity
        {
            Id = Guid.NewGuid(),
            SessionId = null,
            StationId = BuffetSaleConstants.VirtualStationId,
            IsBuffetOnly = true,
        };
        db.OpenSales.Add(sale);
        await audit.AppendAsync("buffet_only.start", new { openSaleId = sale.Id }, ct);
        await db.SaveChangesAsync(ct);
        return sale;
    }

    public async Task<object> AddLineToOpenSaleAsync(Guid openSaleId, string catalogItemId, long? customAmountToman, CancellationToken ct)
    {
        return await WithOpenSaleLockAsync(openSaleId, async (sale, innerCt) =>
        {
            await AddLineCoreAsync(sale, catalogItemId, customAmountToman, innerCt);
            await RefreshOpenSalePendingBillIfAnyAsync(sale, innerCt);
            await audit.AppendAsync("sale.line_add", new { openSaleId, catalogItemId, customAmountToman }, innerCt);
            return await MapSaleWithCustomerAsync(sale, innerCt);
        }, ct);
    }

    public async Task<object> RemoveLineFromOpenSaleAsync(Guid openSaleId, long lineId, CancellationToken ct)
    {
        return await WithOpenSaleLockAsync(openSaleId, async (sale, innerCt) =>
        {
            var line = sale.Lines.FirstOrDefault(l => l.Id == lineId)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Sale line not found.");
            await RemoveLineCoreAsync(line, innerCt);
            await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(innerCt);
            await RefreshOpenSalePendingBillIfAnyAsync(sale, innerCt);
            await audit.AppendAsync("sale.line_remove", new { openSaleId, lineId }, innerCt);
            return await MapSaleWithCustomerAsync(sale, innerCt);
        }, ct);
    }

    public async Task<PendingBillEntity> CheckoutBuffetOnlyAsync(Guid openSaleId, CancellationToken ct)
    {
        return await WithOpenSaleLockAsync(openSaleId, async (sale, innerCt) =>
        {
            if (!sale.IsBuffetOnly)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_buffet_only", "Not a buffet-only sale.");
            }

            if (sale.PendingBill is not null || await db.PendingBills.AnyAsync(b => b.OpenSaleId == sale.Id, innerCt))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "bill_open", "Checkout already started.");
            }

            await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(innerCt);
            if (sale.Lines.Count == 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "empty_sale", "Add at least one line.");
            }

            var frozenAt = clock.UtcNow.UtcDateTime;
            var amounts = await calculator.ComputeBuffetOnlyAsync(sale.Lines, innerCt);
            var bill = new PendingBillEntity
            {
                Id = Guid.NewGuid(),
                OpenSaleId = sale.Id,
                FrozenAtUtc = frozenAt,
                OpenedAtUtc = frozenAt,
                UnroundedPayableToman = amounts.UnroundedPayableToman,
                RoundingToman = amounts.RoundingToman,
                CollectibleToman = amounts.CollectibleToman,
                TimeGrossToman = amounts.TimeGrossToman,
                BuffetGrossToman = amounts.BuffetGrossToman,
                ServicesGrossToman = amounts.ServicesGrossToman,
                Source = "buffet_only",
            };
            db.PendingBills.Add(bill);
            sale.PendingBill = bill;
            await audit.AppendAsync("buffet_only.checkout", new { openSaleId, collectible = bill.CollectibleToman }, innerCt);
            return bill;
        }, ct);
    }

    public async Task<InvoiceEntity> ConfirmBuffetOnlyAsync(
        Guid openSaleId,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        CancellationToken ct)
    {
        return await WithOpenSaleLockAsync(openSaleId, async (sale, innerCt) =>
        {
            var bill = sale.PendingBill
                ?? await db.PendingBills.FirstOrDefaultAsync(b => b.OpenSaleId == sale.Id, innerCt)
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "No bill awaiting confirmation.");
            var tenders = payments.Select(p => new Tender(
                OperatorPaymentMethods.Parse(p.Method),
                p.AmountToman,
                OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet ? sale.CustomerId?.ToString() : null)).ToList();
            var amounts = await calculator.ComputeBuffetOnlyAsync(sale.Lines, sale.CustomerId, tenders, innerCt);
            bill.UnroundedPayableToman = amounts.UnroundedPayableToman;
            bill.RoundingToman = amounts.RoundingToman;
            bill.CollectibleToman = amounts.CollectibleToman;
            bills.ValidatePayments(amounts.CollectibleToman, payments, amounts.Detail, sale.CustomerId);
            await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(innerCt);
            var invoice = await closer.FinalizeBuffetOnlyAsync(sale, bill, payments, amounts, innerCt);
            await audit.AppendAsync("buffet_only.confirm", new { openSaleId, invoice.Number, invoice.CollectibleToman }, innerCt);
            return invoice;
        }, ct);
    }

    public async Task<object> GetBuffetOnlyBillAsync(Guid openSaleId, CancellationToken ct)
    {
        var sale = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .Include(o => o.PendingBill)
            .Include(o => o.Customer)
            .FirstOrDefaultAsync(o => o.Id == openSaleId && o.IsBuffetOnly, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Buffet-only sale not found.");
        var bill = sale.PendingBill;
        return new
        {
            openSaleId = sale.Id,
            lines = OpenSaleMapper.ToDtos(sale.Lines),
            linkedCustomer = CustomerMapper.ToLinkedChip(sale.Customer),
            bill = bill is null ? null : new
            {
                bill.UnroundedPayableToman,
                bill.RoundingToman,
                bill.CollectibleToman,
                bill.TimeGrossToman,
                bill.BuffetGrossToman,
                bill.ServicesGrossToman,
            },
        };
    }

    private async Task<object> MapSaleWithCustomerAsync(OpenSaleEntity sale, CancellationToken ct)
    {
        CustomerEntity? linked = null;
        if (sale.IsBuffetOnly)
        {
            if (sale.CustomerId is Guid customerId)
            {
                linked = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct);
            }
        }
        else if (sale.SessionId is Guid sessionId)
        {
            linked = await db.Sessions.AsNoTracking()
                .Where(s => s.Id == sessionId)
                .Select(s => s.Customer)
                .FirstOrDefaultAsync(ct);
        }

        return MapSale(sale, linked);
    }

    private static object MapSale(OpenSaleEntity sale, CustomerEntity? sessionCustomer = null)
    {
        var buffet = MoneyMath.CheckedSum(sale.Lines.Where(l => l.Kind == SaleLineKind.Buffet).Select(l => l.LineTotalToman));
        var services = MoneyMath.CheckedSum(sale.Lines.Where(l => l.Kind == SaleLineKind.Service).Select(l => l.LineTotalToman));
        var linked = sale.IsBuffetOnly ? sale.Customer : sessionCustomer;
        return new
        {
            openSaleId = sale.Id,
            sessionId = sale.SessionId,
            lines = OpenSaleMapper.ToDtos(sale.Lines),
            timeGrossToman = 0L,
            buffetGrossToman = buffet,
            servicesGrossToman = services,
            linkedCustomer = CustomerMapper.ToLinkedChip(linked),
        };
    }

    private async Task AddLineCoreAsync(OpenSaleEntity sale, string catalogItemId, long? customAmountToman, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""SELECT "Id" FROM catalog_items WHERE "Id" = {0} FOR UPDATE""", catalogItemId);
        }
        catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "catalog_busy", "Catalog item is locked.");
        }

        var item = await db.CatalogItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == catalogItemId && i.IsActive, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Catalog item not found.");
        await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(ct);

        long unitPrice;
        SaleLineKind kind;
        switch (item.Kind)
        {
            case CatalogProductKind.Stocked:
            case CatalogProductKind.Unstocked:
                kind = SaleLineKind.Buffet;
                unitPrice = item.UnitPriceToman;
                if (item.Kind == CatalogProductKind.Stocked)
                {
                    var tracked = await db.CatalogItems.FirstAsync(i => i.Id == catalogItemId, ct);
                    if (tracked.StockQuantity is null or < 1)
                    {
                        throw new ApiException(StatusCodes.Status409Conflict, "insufficient_stock", "Insufficient stock.");
                    }

                    tracked.StockQuantity--;
                }

                break;
            case CatalogProductKind.ServiceFixed:
                kind = SaleLineKind.Service;
                unitPrice = item.UnitPriceToman;
                break;
            case CatalogProductKind.ServiceCustom:
                kind = SaleLineKind.Service;
                if (customAmountToman is not long amount || amount <= 0)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "invalid_money", "Custom service needs a positive integer toman amount.");
                }

                unitPrice = amount;
                break;
            default:
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_catalog", "Unknown item kind.");
        }

        var merge = kind == SaleLineKind.Buffet
            ? sale.Lines.FirstOrDefault(l => l.Kind == SaleLineKind.Buffet && l.CatalogItemId == catalogItemId && l.UnitPriceToman == unitPrice)
            : item.Kind == CatalogProductKind.ServiceFixed
                ? sale.Lines.FirstOrDefault(l => l.Kind == SaleLineKind.Service && l.CatalogItemId == catalogItemId)
                : null;
        if (merge is not null)
        {
            merge.Quantity++;
            merge.LineTotalToman = MoneyMath.CheckedMultiply(merge.Quantity, merge.UnitPriceToman);
        }
        else
        {
            sale.Lines.Add(new OpenSaleLineEntity
            {
                OpenSaleId = sale.Id,
                CatalogItemId = item.Id,
                Kind = kind,
                Quantity = 1,
                UnitPriceToman = unitPrice,
                LineTotalToman = unitPrice,
            });
        }

        await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(ct);
    }

    private async Task RemoveLineCoreAsync(OpenSaleLineEntity line, CancellationToken ct)
    {
        if (line.CatalogItem is null || line.CatalogItem.Kind == CatalogProductKind.Stocked)
        {
            await db.Database.ExecuteSqlRawAsync("""SELECT "Id" FROM catalog_items WHERE "Id" = {0} FOR UPDATE""", line.CatalogItemId);
            var item = await db.CatalogItems.FirstOrDefaultAsync(i => i.Id == line.CatalogItemId, ct);
            if (item?.Kind == CatalogProductKind.Stocked)
            {
                item.StockQuantity = (item.StockQuantity ?? 0) + line.Quantity;
            }
        }

        db.OpenSaleLines.Remove(line);
        line.OpenSale?.Lines.Remove(line);
    }

    private async Task RefreshSessionPendingBillIfAnyAsync(PlaySessionEntity session, OpenSaleEntity sale, CancellationToken ct)
    {
        if (session.PendingBill is null)
        {
            return;
        }

        var bill = session.PendingBill;
        await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(ct);
        var amounts = await calculator.ComputeAsync(session, bill.FrozenAtUtc, bill.SurchargeToman, sale.Lines, ct);
        ApplyAmounts(bill, amounts);
    }

    private async Task RefreshOpenSalePendingBillIfAnyAsync(OpenSaleEntity sale, CancellationToken ct)
    {
        var bill = sale.PendingBill ?? await db.PendingBills.FirstOrDefaultAsync(b => b.OpenSaleId == sale.Id, ct);
        if (bill is null)
        {
            return;
        }

        await db.Entry(sale).Collection(o => o.Lines).Query().Include(l => l.CatalogItem).LoadAsync(ct);
        var amounts = await calculator.ComputeBuffetOnlyAsync(sale.Lines, ct);
        ApplyAmounts(bill, amounts);
    }

    private static void ApplyAmounts(PendingBillEntity bill, BillAmounts amounts)
    {
        bill.UnroundedPayableToman = amounts.UnroundedPayableToman;
        bill.RoundingToman = amounts.RoundingToman;
        bill.CollectibleToman = amounts.CollectibleToman;
        bill.TimeGrossToman = amounts.TimeGrossToman;
        bill.BuffetGrossToman = amounts.BuffetGrossToman;
        bill.ServicesGrossToman = amounts.ServicesGrossToman;
    }

    private async Task<T> WithSessionSaleLockAsync<T>(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync("""SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""", sessionId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "Session is being updated.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .Include(s => s.PendingBill)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Session not found.");
            try
            {
                var result = await action(session, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (policies.ConcurrentBillActionReturns409)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_bill", "Sale was updated elsewhere; refresh and retry.");
            }
        });
    }

    private async Task<T> WithOpenSaleLockAsync<T>(Guid openSaleId, Func<OpenSaleEntity, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await db.Database.ExecuteSqlRawAsync("""SELECT "Id" FROM open_sales WHERE "Id" = {0} FOR UPDATE NOWAIT""", openSaleId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "sale_busy", "Sale is being updated.");
            }

            var sale = await db.OpenSales
                .Include(o => o.PendingBill)
                .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
                .FirstOrDefaultAsync(o => o.Id == openSaleId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Open sale not found.");
            var result = await action(sale, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }
}
