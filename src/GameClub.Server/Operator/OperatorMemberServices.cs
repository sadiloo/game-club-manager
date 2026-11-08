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
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<CustomerMemberEntity>().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            row = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
        }

        return row;
    }

    public async Task<CustomerMemberEntity> ReloadMemberForUpdateAsync(Guid customerId, CancellationToken ct)
    {
        _ = await EnsureMemberAsync(customerId, ct);
        await db.Database.ExecuteSqlRawAsync(
            """SELECT "CustomerId" FROM customer_members WHERE "CustomerId" = {0} FOR UPDATE""",
            customerId);
        var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
        await db.Entry(member).ReloadAsync(ct);
        return member;
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
    OperatorMemberContext members,
    OperatorShiftGate shiftGate)
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

        await members.EnsureMemberAsync(customerId, ct);
        if (method == PaymentMethod.CardToCard)
        {
            await shiftGate.EnsureMoneyRecordingAllowedAsync(ct);
            var c2cShiftId = await shiftGate.RequireOpenShiftIdAsync(ct);
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
                ShiftId = c2cShiftId,
            });
            await audit.AppendAsync("wallet.topup.pending", new { customerId, amountToman, method = method.ToString() }, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var settings = await shiftGate.LoadSettingsAsync(ct);
        Guid? shiftId = null;
        if (settings.ShiftsEnabled)
        {
            var shift = await shiftGate.GetOpenShiftForUpdateAsync(ct)
                ?? throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "no_open_shift",
                    "No shift is open.");
            if (shift.Status != ShiftStatus.Open)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "shift_closed",
                    "Shift is already closed.");
            }

            shiftId = shift.Id;
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", 884_2002);
        }

        await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
        {
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
                ShiftId = shiftId,
            });
            await audit.AppendAsync("wallet.topup", new { customerId, amountToman, method = method.ToString() }, innerCt);
        }, ct, tx);
        if (settings.ShiftsEnabled && shiftId is Guid sid)
        {
            var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (shift is null || shift.Status != ShiftStatus.Open)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "shift_closed", "Shift is already closed.");
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task ConfirmPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        var pendingExists = await db.PendingWalletTopUps.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Pending top-up not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.PendingWalletTopUps
            .Where(p => p.Id == pendingId && p.Status == PendingWalletTopUpStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PendingWalletTopUpStatus.Confirmed),
                ct);
        if (updated == 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Top-up is not pending.");
        }

        var pending = await db.PendingWalletTopUps.AsNoTracking().FirstAsync(p => p.Id == pendingId, ct);
        await WithMemberWriteLockAsync(pending.CustomerId, async (_, member, innerCt) =>
        {
            var before = new { member.RealToman, member.GiftToman };
            var pocket = MemberWalletMapper.ToPocket(member);
            pocket = MemberWalletMapper.ApplyRealTopUp(pocket, pending.AmountToman);
            MemberWalletMapper.ApplyPocket(member, pocket);
            var ledger = new WalletLedgerEntryEntity
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
                ShiftId = pending.ShiftId,
            };
            await ApplyWalletTopUpOriginatingWorkingDayAsync(ledger, pending.ShiftId, pending.CreatedAtUtc, innerCt);
            db.WalletLedger.Add(ledger);
            await audit.AppendAsync("wallet.topup.confirmed", new { pendingId, before, after = new { member.RealToman, member.GiftToman } }, innerCt);
        }, ct, tx);
        await tx.CommitAsync(ct);
    }

    public async Task RejectPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        _ = await db.PendingWalletTopUps.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Pending top-up not found.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.PendingWalletTopUps
            .Where(p => p.Id == pendingId && p.Status == PendingWalletTopUpStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PendingWalletTopUpStatus.Rejected),
                ct);
        if (updated == 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "Top-up is not pending.");
        }

        var pending = await db.PendingWalletTopUps.AsNoTracking().FirstAsync(p => p.Id == pendingId, ct);
        var member = await db.CustomerMembers.AsNoTracking().FirstOrDefaultAsync(m => m.CustomerId == pending.CustomerId, ct);
        var spendableBefore = (member?.RealToman ?? 0) + (member?.GiftToman ?? 0);
        await audit.AppendAsync("wallet.topup.rejected", new { pendingId, spendableBefore }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (member is not null)
        {
            member = await db.CustomerMembers.AsNoTracking().FirstAsync(m => m.CustomerId == pending.CustomerId, ct);
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

        _ = await members.EnsureMemberAsync(customerId, ct);
        await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
        {
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
            await audit.AppendAsync("wallet.gift", new { customerId, giftToman }, innerCt);
        }, ct);
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
        await members.EnsureMemberAsync(customerId, ct);

        if (body.GiftToman is < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_gift", "Opening gift cannot be negative.");
        }

        if (body.LifetimeHours is not null && body.LevelName is not null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "Enter play hours or level, not both.");
        }

        if (body.RealToman is > 0 && body.DebtToman is > 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "Opening cannot include both real money and debt.");
        }

        var hasMoneyFields = body.RealToman is not null || body.GiftToman is not null || body.DebtToman is not null;
        if (!hasMoneyFields && body.LifetimeHours is null && body.LevelName is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "Enter opening amounts or play hours.");
        }

        await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
        {
            if (member.HasOpeningBalance)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "opening_exists", "Opening balance already recorded.");
            }

            var spendableBefore = member.RealToman + member.GiftToman;
            if (!hasMoneyFields && spendableBefore != 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "Enter opening wallet amounts.");
            }

            var before = new
            {
                member.RealToman,
                member.GiftToman,
                hours = member.LifetimePlayMinutes / 60,
            };

            var settings = await members.LoadSettingsAsync(innerCt);
            var ladder = MemberSettingsMapper.LoadLadder(settings);
            long hoursBefore = member.LifetimePlayMinutes / 60;
            if (body.LifetimeHours is long h)
            {
                member.LifetimePlayMinutes = h * 60;
            }
            else if (body.LevelName is string levelName)
            {
                var row = ladder.Levels.FirstOrDefault(l => l.Name.Equals(levelName, StringComparison.Ordinal))
                    ?? throw new ApiException(StatusCodes.Status400BadRequest, "invalid_level", "Unknown level.");
                member.LifetimePlayMinutes = row.MinimumLifetimeHours * 60;
            }

            var realBefore = member.RealToman;
            var giftBefore = member.GiftToman;
            long ledgerReal;
            long ledgerGift = body.GiftToman ?? 0;
            if (body.DebtToman is long debt && debt > 0)
            {
                member.RealToman = MoneyMath.CheckedAdd(realBefore, -debt);
                ledgerReal = -debt;
            }
            else if (body.RealToman is long real)
            {
                member.RealToman = MoneyMath.CheckedAdd(realBefore, real);
                ledgerReal = real;
            }
            else
            {
                ledgerReal = 0;
            }

            if (body.GiftToman is long gift)
            {
                member.GiftToman = MoneyMath.CheckedAdd(giftBefore, gift);
            }

            var pocket = MemberWalletMapper.RefreshSpendBasis(MemberWalletMapper.ToPocket(member));
            MemberWalletMapper.ApplyPocket(member, pocket);
            member.HasOpeningBalance = true;

            if (ledgerReal != 0 || ledgerGift != 0)
            {
                db.WalletLedger.Add(new WalletLedgerEntryEntity
                {
                    CustomerId = customerId,
                    AtUtc = clock.UtcNow.UtcDateTime,
                    Kind = WalletLedgerKind.OpeningBalance,
                    RealDeltaToman = ledgerReal,
                    GiftDeltaToman = ledgerGift,
                    Label = "مانده‌ی افتتاحیه",
                    ActorUserId = op.UserId,
                });
            }

            if (member.LifetimePlayMinutes > hoursBefore * 60)
            {
                db.LoyaltyHourLedger.Add(new LoyaltyHourLedgerEntity
                {
                    CustomerId = customerId,
                    AtUtc = clock.UtcNow.UtcDateTime,
                    DeltaMinutes = member.LifetimePlayMinutes - hoursBefore * 60,
                    Reason = "opening_balance",
                    ActorUserId = op.UserId,
                });
            }

            var after = new
            {
                member.RealToman,
                member.GiftToman,
                hours = member.LifetimePlayMinutes / 60,
            };
            await audit.AppendAsync("member.opening_balance", new { customerId, before, after, body }, innerCt);
        }, ct);
    }

    private async Task WithMemberWriteLockAsync(
        Guid customerId,
        Func<AppDbContext, CustomerMemberEntity, CancellationToken, Task> action,
        CancellationToken ct,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? existingTx = null)
    {
        async Task RunAsync()
        {
            await db.Database.ExecuteSqlRawAsync(
                """SELECT "CustomerId" FROM customer_members WHERE "CustomerId" = {0} FOR UPDATE""",
                customerId);
            var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
            await db.Entry(member).ReloadAsync(ct);
            await action(db, member, ct);
            await db.SaveChangesAsync(ct);
        }

        if (existingTx is not null)
        {
            await RunAsync();
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await RunAsync();
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
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

    public async Task UpdateCombinedDiscountCapAsync(int maxCombinedPercent, CancellationToken ct)
    {
        EnsureManager();
        if (maxCombinedPercent is < 0 or > 100)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_cap", "Combined discount cap must be between 0 and 100.");
        }

        var settings = await db.BranchMemberSettings.FirstAsync(ct);
        settings.MaxCombinedDiscountPercent = maxCombinedPercent;
        await audit.AppendAsync("settings.discount_cap", new { maxCombinedPercent }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> GetCombinedDiscountCapAsync(CancellationToken ct)
    {
        var settings = await members.LoadSettingsAsync(ct);
        return new { maxCombinedPercent = settings.MaxCombinedDiscountPercent };
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

    public async Task<IReadOnlyList<object>> ListPendingTopUpsAsync(CancellationToken ct)
    {
        var rows = await db.PendingWalletTopUps.AsNoTracking()
            .Where(p => p.Status == PendingWalletTopUpStatus.Pending)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        return rows.Select(p => new
        {
            p.Id,
            p.CustomerId,
            amountToman = p.AmountToman,
            method = p.Method.ToString(),
            p.DestinationCardId,
            p.Reference,
            p.CreatedAtUtc,
        }).Cast<object>().ToList();
    }

    public async Task<object> GetBirthdaySettingsAsync(CancellationToken ct)
    {
        var settings = await members.LoadSettingsAsync(ct);
        return new
        {
            percent = settings.BirthdayPercent,
            windowKind = settings.BirthdayWindowKind,
            windowDays = settings.BirthdayWindowDays,
            anchor = settings.BirthdayWindowAnchor,
        };
    }

    private async Task ApplyWalletTopUpOriginatingWorkingDayAsync(
        WalletLedgerEntryEntity row,
        Guid? shiftId,
        DateTime originatingAtUtc,
        CancellationToken ct)
    {
        CloseRecordEntity? close;
        if (shiftId is Guid sid)
        {
            close = await db.CloseRecords.AsNoTracking().FirstOrDefaultAsync(c => c.ShiftId == sid, ct);
        }
        else
        {
            close = await db.CloseRecords.AsNoTracking()
                .Where(c => c.ShiftId == null
                            && c.PeriodOpenedAtUtc <= originatingAtUtc
                            && c.ClosedAtUtc >= originatingAtUtc)
                .OrderByDescending(c => c.ClosedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        if (close is null)
        {
            return;
        }

        row.WorkingDayYear = close.WorkingDayYear;
        row.WorkingDayMonth = close.WorkingDayMonth;
        row.WorkingDayDay = close.WorkingDayDay;
    }

    private void EnsureManager()
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager permission required.");
        }
    }
}

public sealed class OperatorMemberBillingService(AppDbContext db, OperatorMemberContext members)
{
    public async Task ApplyWalletPaymentsAsync(
        Guid customerId,
        CustomerMemberEntity _,
        AllocationResult allocation,
        Guid invoiceId,
        CancellationToken ct)
    {
        if (!allocation.IsValid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "Wallet allocation failed.");
        }

        var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
        await db.Database.ExecuteSqlRawAsync(
            """SELECT "CustomerId" FROM customer_members WHERE "CustomerId" = {0} FOR UPDATE""",
            customerId);
        await db.Entry(member).ReloadAsync(ct);

        var walletTenders = allocation.Tenders.Where(t => t.Method == PaymentMethod.Wallet && t.Accepted).ToList();
        if (walletTenders.Count == 0)
        {
            return;
        }

        var settings = await members.LoadSettingsAsync(ct);
        var debt = MemberSettingsMapper.LoadDebtPolicy(settings);
        var pocket = MemberWalletMapper.ToPocket(member);
        long realDrawn = 0;
        long giftDrawn = 0;
        foreach (var t in walletTenders)
        {
            if (!pocket.TrySpend(t.AmountToman, debt, out pocket, out var gift, out var real))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "Wallet spend failed.");
            }

            realDrawn = MoneyMath.CheckedAdd(realDrawn, real);
            giftDrawn = MoneyMath.CheckedAdd(giftDrawn, gift);
        }

        MemberWalletMapper.ApplyPocket(member, pocket);
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
            await ReversePackagePurchaseAsync(invoice, ct);
            return;
        }

        var customerId = effect.CustomerId;
        var hasWallet = effect.WalletRealDrawnToman != 0 || effect.WalletGiftDrawnToman != 0;

        if (hasWallet)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var member = await members.ReloadMemberForUpdateAsync(customerId, ct);
                member.LifetimePlayMinutes -= effect.LoyaltyMinutesPosted;
                db.LoyaltyHourLedger.Add(new LoyaltyHourLedgerEntity
                {
                    CustomerId = customerId,
                    AtUtc = DateTime.UtcNow,
                    DeltaMinutes = -effect.LoyaltyMinutesPosted,
                    InvoiceId = invoice.Id,
                    Reason = "invoice_void",
                });

                var pocket = MemberWalletMapper.ToPocket(member);
                pocket = WalletPocketMath.Add(pocket, effect.WalletRealDrawnToman, effect.WalletGiftDrawnToman);
                MemberWalletMapper.ApplyPocket(member, pocket);
                db.WalletLedger.Add(new WalletLedgerEntryEntity
                {
                    CustomerId = customerId,
                    AtUtc = DateTime.UtcNow,
                    Kind = WalletLedgerKind.InvoiceVoidReversal,
                    RealDeltaToman = effect.WalletRealDrawnToman,
                    GiftDeltaToman = effect.WalletGiftDrawnToman,
                    InvoiceId = invoice.Id,
                    Label = "void_reversal",
                });

                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        else
        {
            var member = await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == customerId, ct);
            if (member is null)
            {
                return;
            }

            member.LifetimePlayMinutes -= effect.LoyaltyMinutesPosted;
            db.LoyaltyHourLedger.Add(new LoyaltyHourLedgerEntity
            {
                CustomerId = customerId,
                AtUtc = DateTime.UtcNow,
                DeltaMinutes = -effect.LoyaltyMinutesPosted,
                InvoiceId = invoice.Id,
                Reason = "invoice_void",
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

        await ReversePackagePurchaseAsync(invoice, ct);
    }

    private async Task ReversePackagePurchaseAsync(InvoiceEntity invoice, CancellationToken ct)
    {
        if (invoice.CustomerId is not Guid customerId)
        {
            return;
        }

        var pkg = await db.CustomerPackages.FirstOrDefaultAsync(
            p => p.PurchaseInvoiceId == invoice.Id,
            ct);
        if (pkg is null)
        {
            return;
        }

        db.CustomerPackages.Remove(pkg);

        var purchaseLedger = await db.WalletLedger
            .Where(e => e.InvoiceId == invoice.Id && e.Kind == WalletLedgerKind.PackagePurchase)
            .ToListAsync(ct);
        if (purchaseLedger.Count == 0 && invoice.PaymentLines.All(p => p.Method != PaymentMethod.Wallet))
        {
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = DateTime.UtcNow,
                Kind = WalletLedgerKind.InvoiceVoidReversal,
                InvoiceId = invoice.Id,
                Label = "package_sale_void",
            });
            return;
        }

        long realRestore = 0;
        long giftRestore = 0;
        foreach (var entry in purchaseLedger)
        {
            realRestore = MoneyMath.CheckedAdd(realRestore, -entry.RealDeltaToman);
            giftRestore = MoneyMath.CheckedAdd(giftRestore, -entry.GiftDeltaToman);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            _ = await members.ReloadMemberForUpdateAsync(customerId, ct);
            if (realRestore != 0 || giftRestore != 0)
            {
                var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
                var pocket = MemberWalletMapper.ToPocket(member);
                pocket = WalletPocketMath.Add(pocket, realRestore, giftRestore);
                MemberWalletMapper.ApplyPocket(member, pocket);
            }

            foreach (var entry in purchaseLedger)
            {
                db.WalletLedger.Add(new WalletLedgerEntryEntity
                {
                    CustomerId = customerId,
                    AtUtc = DateTime.UtcNow,
                    Kind = WalletLedgerKind.InvoiceVoidReversal,
                    RealDeltaToman = -entry.RealDeltaToman,
                    GiftDeltaToman = -entry.GiftDeltaToman,
                    InvoiceId = invoice.Id,
                    Label = "package_sale_void",
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}

public sealed class OperatorPackageService(
    AppDbContext db,
    AuditService audit,
    OperatorMemberContext members,
    OperatorContext op,
    IClock clock,
    OperatorShiftGate shiftGate)
{
    public async Task SellPackageAsync(Guid customerId, string productId, IReadOnlyList<OperatorBillService.PaymentInput> payments, CancellationToken ct)
    {
        var product = await db.PackageProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId && p.IsActive, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Package not found.");

        await shiftGate.EnsureMoneyRecordingAllowedAsync(ct);
        var shiftId = await shiftGate.RequireOpenShiftIdAsync(ct);
        await members.EnsureMemberAsync(customerId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """SELECT "CustomerId" FROM customer_members WHERE "CustomerId" = {0} FOR UPDATE""",
            customerId);
        var member = await db.CustomerMembers.FirstAsync(m => m.CustomerId == customerId, ct);
        await db.Entry(member).ReloadAsync(ct);

        var tenders = payments.Select(p => new Tender(OperatorPaymentMethods.Parse(p.Method), p.AmountToman, customerId.ToString())).ToList();
        var split = PaymentSplitter.Split(product.PriceToman, tenders, [
            MemberWalletMapper.ToMemberWallet(customerId, member),
        ]);
        if (!split.IsValid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", split.Errors.FirstOrDefault()?.Message ?? "Invalid payment.");
        }

        var invoiceId = Guid.NewGuid();
        var number = await NextInvoiceNumberAsync(ct);
        var invoice = new InvoiceEntity
        {
            Id = invoiceId,
            Number = number,
            CustomerId = customerId,
            TimeGrossToman = 0,
            BuffetGrossToman = 0,
            ServicesGrossToman = 0,
            UnroundedPayableToman = product.PriceToman,
            RoundingToman = 0,
            CollectibleToman = product.PriceToman,
            IssuedAtUtc = clock.UtcNow.UtcDateTime,
            BilledToUtc = clock.UtcNow.UtcDateTime,
            Status = "confirmed",
            ShiftId = shiftId,
            SettledByUserId = op.UserId,
        };
        invoice.CatalogLines.Add(new InvoiceCatalogLineEntity
        {
            CatalogItemId = productId,
            Name = product.Name,
            Kind = SaleLineKind.Service,
            Quantity = 1,
            UnitPriceToman = product.PriceToman,
            LineTotalToman = product.PriceToman,
        });
        foreach (var pay in payments)
        {
            var method = OperatorPaymentMethods.Parse(pay.Method);
            var splitLine = split.Tenders.FirstOrDefault(t => t.Method == method && t.AmountToman == pay.AmountToman && t.Accepted);
            invoice.PaymentLines.Add(new PaymentLineEntity
            {
                Method = method,
                AmountToman = pay.AmountToman,
                ConfirmationStatus = method == PaymentMethod.CardToCard
                    ? PaymentConfirmationStatus.Pending
                    : PaymentConfirmationStatus.Settled,
                WalletCustomerId = method == PaymentMethod.Wallet ? customerId : null,
                WalletRealDrawnToman = splitLine?.RealDrawnToman ?? 0,
                WalletGiftDrawnToman = splitLine?.GiftDrawnToman ?? 0,
            });
        }

        db.Invoices.Add(invoice);

        var walletTender = split.Tenders.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted);
        if (walletTender is not null)
        {
            var pocket = MemberWalletMapper.ToPocket(member);
            pocket = WalletPocketMath.Add(pocket, -walletTender.RealDrawnToman, -walletTender.GiftDrawnToman);
            MemberWalletMapper.ApplyPocket(member, pocket);
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = clock.UtcNow.UtcDateTime,
                Kind = WalletLedgerKind.PackagePurchase,
                RealDeltaToman = -walletTender.RealDrawnToman,
                GiftDeltaToman = -walletTender.GiftDrawnToman,
                Method = PaymentMethod.Wallet,
                Label = "package",
                InvoiceId = invoiceId,
                ActorUserId = op.UserId,
            });
        }
        else
        {
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = clock.UtcNow.UtcDateTime,
                Kind = WalletLedgerKind.PackagePurchase,
                Method = payments.Select(p => OperatorPaymentMethods.Parse(p.Method)).First(),
                Label = "package",
                InvoiceId = invoiceId,
                ActorUserId = op.UserId,
            });
        }

        var pkgId = Guid.NewGuid();
        db.CustomerPackages.Add(new CustomerPackageEntity
        {
            Id = pkgId,
            CustomerId = customerId,
            Name = product.Name,
            RemainingMinutes = product.Minutes,
            FaceValueToman = product.PriceToman,
            AllowedGroupIdsJson = product.AllowedGroupIdsJson,
            ExpiresAtUtc = product.ExpiresAfterDays is int days
                ? clock.UtcNow.UtcDateTime.AddDays(days)
                : null,
            PurchaseInvoiceId = invoiceId,
        });

        await audit.AppendAsync("package.sold", new { customerId, productId, product.Minutes, pkgId, invoiceId, payments }, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task<long> NextInvoiceNumberAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT nextval('invoice_number_seq')";
            var value = await cmd.ExecuteScalarAsync(ct);
            return Convert.ToInt64(value);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private void EnsureManager()
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "Manager permission required.");
        }
    }

    public async Task<IReadOnlyList<object>> ListProductsAsync(CancellationToken ct)
    {
        var rows = await db.PackageProducts.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        return rows.Select(p => new
        {
            p.Id,
            p.Name,
            p.PriceToman,
            p.Minutes,
            p.ExpiresAfterDays,
            p.AllowedGroupIdsJson,
        }).Cast<object>().ToList();
    }

    public async Task UpsertProductAsync(
        string id,
        string name,
        long priceToman,
        long minutes,
        int? expiresAfterDays,
        string? allowedGroupIdsJson,
        CancellationToken ct)
    {
        EnsureManager();
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_id", "Package id is required.");
        }

        if (priceToman <= 0 || minutes <= 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_product", "Price and minutes must be positive.");
        }

        var row = await db.PackageProducts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null)
        {
            row = new PackageProductEntity { Id = id };
            db.PackageProducts.Add(row);
        }

        row.Name = name;
        row.PriceToman = priceToman;
        row.Minutes = minutes;
        row.ExpiresAfterDays = expiresAfterDays;
        row.AllowedGroupIdsJson = allowedGroupIdsJson;
        row.IsActive = true;
        await db.SaveChangesAsync(ct);
    }
}
