using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed class OperatorMemberContext(AppDbContext db)
{
    public async Task<BranchMemberSettingsEntity> LoadSettingsAsync(CancellationToken ct)
    {
        var row = await db.BranchMemberSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (row is null)
        {
            row = new BranchMemberSettingsEntity
            {
                LevelLadderJson = MemberSettingsMapper.SerializeLadder(LevelLadder.Default),
            };
            db.BranchMemberSettings.Add(row);
            await db.SaveChangesAsync(ct);
        }

        return row;
    }

    public async Task<CustomerMemberEntity> EnsureMemberAsync(Guid customerId, CancellationToken ct)
    {
        var row = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == customerId, ct);
        if (row is not null)
        {
            return row;
        }

        row = new CustomerMemberEntity { CustomerId = customerId };
        db.CustomerMembers.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<(CustomerMemberEntity? Member, IReadOnlyList<TimePackage> Packages)> LoadMemberStateAsync(Guid? customerId, CancellationToken ct)
    {
        if (customerId is not Guid id)
        {
            return (null, []);
        }

        var member = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == id, ct);
        if (member is null)
        {
            return (null, []);
        }

        var pkgRows = await db.CustomerPackages.Where(p => p.CustomerId == id).ToListAsync(ct);
        var packages = pkgRows.Select(MapPackage).ToArray();
        return (member, packages);
    }

    private static TimePackage MapPackage(CustomerPackageEntity row)
    {
        IReadOnlySet<string>? groups = null;
        if (!string.IsNullOrWhiteSpace(row.AllowedGroupIdsJson))
        {
            var ids = JsonSerializer.Deserialize<string[]>(row.AllowedGroupIdsJson) ?? [];
            groups = new HashSet<string>(ids, StringComparer.Ordinal);
        }

        LocalDate? validThrough = row.ExpiresAtUtc is DateTime exp
            ? TehranTime.Date(TehranTime.FromUtc(DateTime.SpecifyKind(exp, DateTimeKind.Utc)))
            : null;

        return new TimePackage(
            row.Id.ToString(),
            row.RemainingMinutes,
            row.FaceValueToman,
            groups,
            null,
            null,
            validThrough);
    }
}

public sealed class OperatorMemberWalletService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    OperatorContext op,
    OperatorMemberContext members)
{
    public async Task<object> GetWalletAsync(Guid customerId, CancellationToken ct)
    {
        var member = await members.EnsureMemberAsync(customerId, ct);
        var hours = member.LifetimePlayMinutes / 60;
        var settings = await members.LoadSettingsAsync(ct);
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        var level = ladder.Resolve(hours);
        return new
        {
            customerId,
            realToman = member.RealToman,
            giftToman = member.GiftToman,
            spendableToman = member.RealToman + member.GiftToman,
            lifetimeHours = hours,
            levelName = level?.Name,
            levelPercent = level?.DiscountPercent ?? 0,
        };
    }

    public async Task TopUpAsync(
        Guid customerId,
        string methodRaw,
        long amountToman,
        string? destinationCardId,
        string? reference,
        CancellationToken ct)
    {
        if (amountToman <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Amount must be positive.");
        }

        var method = OperatorPaymentMethods.Parse(methodRaw);
        if (method == PaymentMethod.Wallet)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_method", "Invalid top-up method.");
        }

        var member = await members.EnsureMemberAsync(customerId, ct);
        if (method == PaymentMethod.CardToCard)
        {
            db.PendingWalletTopUps.Add(new PendingWalletTopUpEntity
            {
                CustomerId = customerId,
                AmountToman = amountToman,
                Method = method,
                DestinationCardId = destinationCardId,
                Reference = reference,
                Status = PendingWalletTopUpStatus.Pending,
                CreatedAtUtc = clock.UtcNow.UtcDateTime,
                ActorUserId = op.UserId,
            });
            await audit.AppendAsync("wallet.topup.pending", new { customerId, amountToman, method = method.ToString() }, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        var pocket = MemberWalletMapper.ToPocket(member);
        pocket = MemberWalletMapper.ApplyRealTopUp(pocket, amountToman);
        MemberWalletMapper.ApplyPocket(member, pocket);
        db.WalletLedger.Add(new WalletLedgerEntryEntity
        {
            CustomerId = customerId,
            AtUtc = clock.UtcNow.UtcDateTime,
            Kind = WalletLedgerKind.TopUpReal,
            RealDeltaToman = amountToman,
            Method = method,
            Reference = reference,
            Label = method.ToString(),
            ActorUserId = op.UserId,
        });
        await audit.AppendAsync("wallet.topup", new { customerId, amountToman, method = method.ToString() }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task ConfirmPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        var pending = await db.PendingWalletTopUps.FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Pending top-up not found.");
        if (pending.Status != PendingWalletTopUpStatus.Pending)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Top-up is not pending.");
        }

        var member = await members.EnsureMemberAsync(pending.CustomerId, ct);
        var before = new { member.RealToman, member.GiftToman };
        var pocket = MemberWalletMapper.ToPocket(member);
        pocket = MemberWalletMapper.ApplyRealTopUp(pocket, pending.AmountToman);
        MemberWalletMapper.ApplyPocket(member, pocket);
        pending.Status = PendingWalletTopUpStatus.Confirmed;
        db.WalletLedger.Add(new WalletLedgerEntryEntity
        {
            CustomerId = pending.CustomerId,
            AtUtc = clock.UtcNow.UtcDateTime,
            Kind = WalletLedgerKind.TopUpReal,
            RealDeltaToman = pending.AmountToman,
            Method = pending.Method,
            DestinationCardId = pending.DestinationCardId,
            Reference = pending.Reference,
            Label = "card_to_card_confirmed",
            ActorUserId = op.UserId,
        });
        await audit.AppendAsync("wallet.topup.confirmed", new { pendingId, before, after = new { member.RealToman, member.GiftToman } }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task RejectPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        var pending = await db.PendingWalletTopUps.FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Pending top-up not found.");
        if (pending.Status != PendingWalletTopUpStatus.Pending)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Top-up is not pending.");
        }

        var member = await db.CustomerMembers.AsNoTracking().FirstOrDefaultAsync(m => m.CustomerId == pending.CustomerId, ct);
        var spendableBefore = (member?.RealToman ?? 0) + (member?.GiftToman ?? 0);
        pending.Status = PendingWalletTopUpStatus.Rejected;
        await audit.AppendAsync("wallet.topup.rejected", new { pendingId, spendableBefore }, ct);
        await db.SaveChangesAsync(ct);
        if (member is not null)
        {
            var spendableAfter = member.RealToman + member.GiftToman;
            if (spendableAfter != spendableBefore)
            {
                throw new InvalidOperationException("Rejected pending top-up changed spendable balance.");
            }
        }
    }

    public async Task IssueGiftAsync(Guid customerId, long giftToman, CancellationToken ct)
    {
        EnsureManager();
        if (giftToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Gift amount cannot be negative.");
        }

        var member = await members.EnsureMemberAsync(customerId, ct);
        var pocket = MemberWalletMapper.ToPocket(member);
        pocket = MemberWalletMapper.ApplyGiftTopUp(pocket, giftToman);
        MemberWalletMapper.ApplyPocket(member, pocket);
        db.WalletLedger.Add(new WalletLedgerEntryEntity
        {
            CustomerId = customerId,
            AtUtc = clock.UtcNow.UtcDateTime,
            Kind = WalletLedgerKind.TopUpGift,
            GiftDeltaToman = giftToman,
            Label = "gift",
            ActorUserId = op.UserId,
        });
        await audit.AppendAsync("wallet.gift", new { customerId, giftToman }, ct);
        await db.SaveChangesAsync(ct);
    }

    public sealed record OpeningBalanceRequest(
        long? RealToman,
        long? GiftToman,
        long? DebtToman,
        long? LifetimeHours,
        string? LevelName);

    public async Task RecordOpeningBalanceAsync(Guid customerId, OpeningBalanceRequest body, CancellationToken ct)
    {
        EnsureManager();
        var member = await members.EnsureMemberAsync(customerId, ct);
        if (member.HasOpeningBalance)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "opening_exists", "Opening balance already recorded.");
        }

        var settings = await members.LoadSettingsAsync(ct);
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        long hours = member.LifetimePlayMinutes / 60;
        if (body.LifetimeHours is long h)
        {
            hours = h;
            member.LifetimePlayMinutes = h * 60;
        }
        else if (body.LevelName is string levelName)
        {
            var row = ladder.Levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.Ordinal))
                ?? throw new ApiException(StatusCodes.Status400BadRequest, "invalid_level", "Unknown level.");
            hours = row.MinimumLifetimeHours;
            member.LifetimePlayMinutes = hours * 60;
        }

        if (body.RealToman is long real)
        {
            member.RealToman = real;
        }

        if (body.GiftToman is long gift)
        {
            member.GiftToman = gift;
        }

        if (body.DebtToman is long debt && debt > 0)
        {
            member.RealToman = -debt;
        }

        var posReal = member.RealToman > 0 ? member.RealToman : 0;
        var posGift = member.GiftToman > 0 ? member.GiftToman : 0;
        member.BasisRealToman = posReal;
        member.BasisGiftToman = posGift;
        member.PositiveDrawnToman = 0;
        member.GiftDrawnFromBasisToman = 0;
        member.HasOpeningBalance = true;

        db.WalletLedger.Add(new WalletLedgerEntryEntity
        {
            CustomerId = customerId,
            AtUtc = clock.UtcNow.UtcDateTime,
            Kind = WalletLedgerKind.OpeningBalance,
            RealDeltaToman = body.RealToman ?? 0,
            GiftDeltaToman = body.GiftToman ?? 0,
            Label = "مانده‌ی افتتاحیه",
            ActorUserId = op.UserId,
        });
        await audit.AppendAsync("member.opening_balance", new { customerId, body }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetDebtFloorAsync(long? floorToman, CancellationToken ct)
    {
        EnsureManager();
        var settings = await db.BranchMemberSettings.FirstAsync(ct);
        if (floorToman is > 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_floor", "Debt floor must be zero or negative.");
        }

        settings.WalletDebtFloorToman = floorToman;
        await audit.AppendAsync("settings.debt_floor", new { floorToman }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateLadderAsync(IReadOnlyList<CustomerLevel> levels, CancellationToken ct)
    {
        EnsureManager();
        var ladder = LevelLadder.Create(levels);
        var settings = await db.BranchMemberSettings.FirstAsync(ct);
        settings.LevelLadderJson = MemberSettingsMapper.SerializeLadder(ladder);
        await audit.AppendAsync("settings.level_ladder", new { count = levels.Count }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateBirthdaySettingsAsync(int percent, string windowKind, int windowDays, string anchor, CancellationToken ct)
    {
        EnsureManager();
        var settings = await db.BranchMemberSettings.FirstAsync(ct);
        settings.BirthdayPercent = percent;
        settings.BirthdayWindowKind = windowKind;
        settings.BirthdayWindowDays = windowDays;
        settings.BirthdayWindowAnchor = anchor;
        await audit.AppendAsync("settings.birthday", new { percent, windowKind }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetCustomerBirthdayAsync(Guid customerId, int year, int month, int day, CancellationToken ct)
    {
        EnsureManager();
        _ = new JalaliDate(year, month, day);
        var member = await members.EnsureMemberAsync(customerId, ct);
        member.BirthYear = year;
        member.BirthMonth = month;
        member.BirthDay = day;
        await audit.AppendAsync("member.birthday", new { customerId, year, month, day }, ct);
        await db.SaveChangesAsync(ct);
    }

    private void EnsureManager()
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager permission required.");
        }
    }
}

public sealed class OperatorMemberBillingService(AppDbContext db)
{
    public async Task ApplyWalletPaymentsAsync(
        Guid customerId,
        CustomerMemberEntity member,
        AllocationResult allocation,
        Guid invoiceId,
        CancellationToken ct)
    {
        if (!allocation.IsValid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "Wallet allocation failed.");
        }

        var walletResult = allocation.Tenders.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted);
        var remaining = allocation.RemainingWallets.FirstOrDefault(w => w.MemberId == customerId.ToString());
        if (remaining is not null)
        {
            MemberWalletMapper.ApplyPocket(member, remaining.Pocket);
        }

        long realDrawn = 0;
        long giftDrawn = 0;
        foreach (var t in allocation.Tenders.Where(t => t.Method == PaymentMethod.Wallet && t.Accepted))
        {
            realDrawn = MoneyMath.CheckedAdd(realDrawn, t.RealDrawnToman);
            giftDrawn = MoneyMath.CheckedAdd(giftDrawn, t.GiftDrawnToman);
        }

        if (realDrawn != 0 || giftDrawn != 0)
        {
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = DateTime.UtcNow,
                Kind = WalletLedgerKind.InvoiceSpend,
                RealDeltaToman = -realDrawn,
                GiftDeltaToman = -giftDrawn,
                InvoiceId = invoiceId,
                Label = "invoice",
            });
        }
    }

    public async Task PostConfirmEffectsAsync(
        Guid? customerId,
        CustomerMemberEntity? member,
        MemberBillResult bill,
        Guid invoiceId,
        long billedMinutes,
        CancellationToken ct)
    {
        if (customerId is not Guid id || member is null)
        {
            return;
        }

        if (bill.PackageSettlement is not null)
        {
            foreach (var draw in bill.PackageSettlement.PackageDraws)
            {
                if (Guid.TryParse(draw.PackageId, out var pkgId))
                {
                    var row = await db.CustomerPackages.FirstOrDefaultAsync(p => p.Id == pkgId, ct);
                    if (row is not null)
                    {
                        row.RemainingMinutes -= draw.Minutes;
                    }
                }
            }
        }

        if (billedMinutes > 0)
        {
            member.LifetimePlayMinutes = MoneyMath.CheckedAdd(member.LifetimePlayMinutes, billedMinutes);
            db.LoyaltyHourLedger.Add(new LoyaltyHourLedgerEntity
            {
                CustomerId = id,
                AtUtc = DateTime.UtcNow,
                DeltaMinutes = billedMinutes,
                InvoiceId = invoiceId,
                Reason = "invoice_confirm",
                ActorUserId = null,
            });
        }

        db.InvoiceMemberEffects.Add(new InvoiceMemberEffectEntity
        {
            InvoiceId = invoiceId,
            CustomerId = id,
            WalletRealDrawnToman = bill.Allocation?.Tenders.Where(t => t.Method == PaymentMethod.Wallet).Sum(t => t.RealDrawnToman) ?? 0,
            WalletGiftDrawnToman = bill.Allocation?.Tenders.Where(t => t.Method == PaymentMethod.Wallet).Sum(t => t.GiftDrawnToman) ?? 0,
            LoyaltyMinutesPosted = billedMinutes,
            PackageDrawsJson = bill.PackageSettlement is null
                ? null
                : JsonSerializer.Serialize(bill.PackageSettlement.PackageDraws),
        });
    }

    public async Task ReverseInvoiceEffectsAsync(InvoiceEntity invoice, CancellationToken ct)
    {
        var effect = await db.InvoiceMemberEffects.FirstOrDefaultAsync(e => e.InvoiceId == invoice.Id, ct);
        if (effect is null)
        {
            return;
        }

        var member = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == effect.CustomerId, ct);
        if (member is null)
        {
            return;
        }

        member.LifetimePlayMinutes -= effect.LoyaltyMinutesPosted;
        db.LoyaltyHourLedger.Add(new LoyaltyHourLedgerEntity
        {
            CustomerId = effect.CustomerId,
            AtUtc = DateTime.UtcNow,
            DeltaMinutes = -effect.LoyaltyMinutesPosted,
            InvoiceId = invoice.Id,
            Reason = "invoice_void",
        });

        if (effect.WalletRealDrawnToman != 0 || effect.WalletGiftDrawnToman != 0)
        {
            var pocket = MemberWalletMapper.ToPocket(member);
            pocket = WalletPocketMath.Add(pocket, effect.WalletRealDrawnToman, effect.WalletGiftDrawnToman);
            MemberWalletMapper.ApplyPocket(member, pocket);
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = effect.CustomerId,
                AtUtc = DateTime.UtcNow,
                Kind = WalletLedgerKind.InvoiceVoidReversal,
                RealDeltaToman = effect.WalletRealDrawnToman,
                GiftDeltaToman = effect.WalletGiftDrawnToman,
                InvoiceId = invoice.Id,
                Label = "void_reversal",
            });
        }

        if (!string.IsNullOrWhiteSpace(effect.PackageDrawsJson))
        {
            var draws = JsonSerializer.Deserialize<PackageDraw[]>(effect.PackageDrawsJson) ?? [];
            foreach (var draw in draws)
            {
                if (Guid.TryParse(draw.PackageId, out var pkgId))
                {
                    var row = await db.CustomerPackages.FirstOrDefaultAsync(p => p.Id == pkgId, ct);
                    if (row is not null)
                    {
                        row.RemainingMinutes += draw.Minutes;
                    }
                }
            }
        }
    }
}

public sealed class OperatorPackageService(
    AppDbContext db,
    AuditService audit,
    OperatorMemberContext members)
{
    public async Task SellPackageAsync(Guid customerId, string productId, IReadOnlyList<OperatorBillService.PaymentInput> payments, CancellationToken ct)
    {
        var product = await db.PackageProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.IsActive, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Package not found.");

        await members.EnsureMemberAsync(customerId, ct);
        var tenders = payments.Select(p => new Tender(OperatorPaymentMethods.Parse(p.Method), p.AmountToman, customerId.ToString())).ToList();
        var split = PaymentSplitter.Split(product.PriceToman, tenders, [
            MemberWalletMapper.ToMemberWallet(customerId, await members.EnsureMemberAsync(customerId, ct)),
        ]);
        if (!split.IsValid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", split.Errors.FirstOrDefault()?.Message ?? "Invalid payment.");
        }

        var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
        var walletTender = split.Tenders.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted);
        if (walletTender is not null)
        {
            var pocket = MemberWalletMapper.ToPocket(member);
            if (!pocket.TrySpend(walletTender.AmountToman, DebtPolicy.None, out var updated, out _, out _))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "insufficient_wallet", "Insufficient wallet.");
            }

            MemberWalletMapper.ApplyPocket(member, updated);
        }

        db.CustomerPackages.Add(new CustomerPackageEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Name = product.Name,
            RemainingMinutes = product.Minutes,
            FaceValueToman = product.PriceToman,
            AllowedGroupIdsJson = product.AllowedGroupIdsJson,
        });
        await audit.AppendAsync("package.sold", new { customerId, productId, product.Minutes }, ct);
        await db.SaveChangesAsync(ct);
    }
}
