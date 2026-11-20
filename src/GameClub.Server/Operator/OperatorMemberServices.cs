using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    public async Task<CustomerMemberEntity?> LoadMemberAsync(Guid? customerId, CancellationToken ct)
    {
        if (customerId is not Guid id)
        {
            return null;
        }

        return await db.CustomerMembers.FirstOrDefaultAsync(m => m.CustomerId == id, ct);
    }
}

public sealed partial class OperatorMemberWalletService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    OperatorContext op,
    OperatorMemberContext members,
    OperatorShiftGate shiftGate,
    IServiceScopeFactory scopeFactory)
{
    private async Task<object> ResolveWalletCommandUniqueViolationAsync(
        Guid commandId,
        string fingerprint,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var freshDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await WalletCommandSupport.HandleUniqueViolationAsync(freshDb, commandId, fingerprint, ct);
    }
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

    public async Task<object> TopUpAsync(
        Guid customerId,
        string methodRaw,
        long amountToman,
        long giftToman,
        string? destinationCardId,
        string? reference,
        Guid? commandId,
        CancellationToken ct)
    {
        WalletCommandSupport.EnsureCommandIdPresent(commandId);
        var cmdId = commandId!.Value;

        OperatorMoneyValidation.EnsurePositiveBounded(amountToman);
        OperatorMoneyValidation.EnsureNonNegativeBounded(giftToman, "invalid_gift");

        var memberSettings = await members.LoadSettingsAsync(ct);

        var method = OperatorPaymentMethods.Parse(methodRaw);
        if (method == PaymentMethod.Wallet)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_method", "روش شارژ نامعتبر است.");
        }

        var methodName = method.ToString();
        var fingerprint = WalletCommandSupport.BuildTopUpFingerprint(customerId, methodName, amountToman, giftToman);
        var replay = await WalletCommandSupport.TryReplayOrThrowReuseAsync(db, cmdId, fingerprint, ct);
        if (replay is not null)
        {
            return replay;
        }

        WalletGiftCapMath.ValidateOperatorGiftOrThrow(op.Role ?? UserRole.Operator, amountToman, giftToman, memberSettings);

        await members.EnsureMemberAsync(customerId, ct);
        if (method == PaymentMethod.CardToCard)
        {
            await shiftGate.EnsureMoneyRecordingAllowedAsync(ct);
            var c2cShiftId = await shiftGate.RequireOpenShiftIdAsync(ct);
            await using var pendingTx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
                {
                    var pendingReplay = await WalletCommandSupport.TryReplayOrThrowReuseAsync(db, cmdId, fingerprint, innerCt);
                    if (pendingReplay is not null)
                    {
                        throw new WalletCommandReplayException(pendingReplay);
                    }

                    WalletGiftCapMath.ValidateGiftNotBlockedByDebt(member.RealToman, amountToman, giftToman);
                    db.PendingWalletTopUps.Add(new PendingWalletTopUpEntity
                    {
                        CustomerId = customerId,
                        AmountToman = amountToman,
                        GiftToman = giftToman,
                        Method = method,
                        DestinationCardId = destinationCardId,
                        Reference = reference,
                        Status = PendingWalletTopUpStatus.Pending,
                        CreatedAtUtc = clock.UtcNow.UtcDateTime,
                        ActorUserId = op.UserId,
                        ShiftId = c2cShiftId,
                    });
                    await audit.AppendAsync("wallet.topup.pending", new { customerId, amountToman, giftToman, method = methodName }, innerCt);
                    var walletJson = JsonSerializer.Serialize(await BuildWalletSnapshotAsync(customerId, innerCt));
                    WalletCommandSupport.RecordWalletCommand(
                        db,
                        clock,
                        cmdId,
                        WalletCommandTypes.TopUpPending,
                        customerId,
                        fingerprint,
                        walletJson);
                    try
                    {
                        await db.SaveChangesAsync(innerCt);
                    }
                    catch (DbUpdateException ex) when (PostgresErrors.IsWalletCommandUniqueViolation(ex))
                    {
                        throw new WalletCommandUniqueViolationException(cmdId, fingerprint);
                    }

                    var pending = db.PendingWalletTopUps.Local.OrderByDescending(p => p.Id).First();
                    var cmd = db.WalletCommands.Local.Single(c => c.CommandId == cmdId);
                    cmd.PendingWalletTopUpId = pending.Id;
                    await db.SaveChangesAsync(innerCt);
                }, ct, pendingTx);

                await pendingTx.CommitAsync(ct);
            }
            catch (WalletCommandUniqueViolationException uve)
            {
                await pendingTx.RollbackAsync(ct);
                return await ResolveWalletCommandUniqueViolationAsync(uve.CommandId, uve.Fingerprint, ct);
            }
            catch (WalletCommandReplayException replayEx)
            {
                await pendingTx.RollbackAsync(ct);
                return replayEx.Body;
            }

            var storedPending = await WalletCommandSupport.TryReplayAsync(db, cmdId, ct);
            return storedPending ?? await GetWalletAsync(customerId, ct);
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
                    "شیفت بازی باز نیست.");
            if (shift.Status != ShiftStatus.Open)
            {
                throw new ApiException(
                    StatusCodes.Status409Conflict,
                    "shift_closed",
                    "شیفت قبلاً بسته شده است.");
            }

            shiftId = shift.Id;
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", 884_2002);
        }

        var topUpGroupId = Guid.NewGuid();
        object? result = null;
        try
        {
            await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
            {
                var topUpReplay = await WalletCommandSupport.TryReplayOrThrowReuseAsync(db, cmdId, fingerprint, innerCt);
                if (topUpReplay is not null)
                {
                    result = topUpReplay;
                    return;
                }

                WalletGiftCapMath.ValidateGiftNotBlockedByDebt(member.RealToman, amountToman, giftToman);
                var pocket = MemberWalletMapper.ToPocket(member);
                pocket = MemberWalletMapper.ApplyRealTopUp(pocket, amountToman);
                if (giftToman > 0)
                {
                    pocket = MemberWalletMapper.ApplyGiftTopUp(pocket, giftToman);
                }

                MemberWalletMapper.ApplyPocket(member, pocket);
                WriteTopUpLedgerPair(customerId, amountToman, giftToman, method, null, reference, topUpGroupId, shiftId);
                await audit.AppendAsync("wallet.topup", new { customerId, amountToman, giftToman, method = methodName }, innerCt);
                var walletJson = JsonSerializer.Serialize(await BuildWalletSnapshotAsync(customerId, innerCt));
                WalletCommandSupport.RecordWalletCommand(
                    db,
                    clock,
                    cmdId,
                    WalletCommandTypes.TopUp,
                    customerId,
                    fingerprint,
                    walletJson,
                    topUpGroupId);
                try
                {
                    await db.SaveChangesAsync(innerCt);
                }
                catch (DbUpdateException ex) when (PostgresErrors.IsWalletCommandUniqueViolation(ex))
                {
                    throw new WalletCommandUniqueViolationException(cmdId, fingerprint);
                }
            }, ct, tx);
        }
        catch (WalletCommandUniqueViolationException uve)
        {
            await tx.RollbackAsync(ct);
            return await ResolveWalletCommandUniqueViolationAsync(uve.CommandId, uve.Fingerprint, ct);
        }
        catch (WalletCommandReplayException replayEx)
        {
            await tx.RollbackAsync(ct);
            return replayEx.Body;
        }

        if (result is not null)
        {
            await tx.RollbackAsync(ct);
            return result;
        }

        if (settings.ShiftsEnabled && shiftId is Guid sid)
        {
            var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            if (shift is null || shift.Status != ShiftStatus.Open)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "shift_closed", "شیفت قبلاً بسته شده است.");
            }
        }

        await tx.CommitAsync(ct);
        var stored = await WalletCommandSupport.TryReplayAsync(db, cmdId, ct);
        return stored ?? await GetWalletAsync(customerId, ct);
    }

    private sealed class WalletCommandReplayException(object body) : Exception
    {
        public object Body { get; } = body;
    }

    private sealed class WalletCommandUniqueViolationException(Guid commandId, string fingerprint) : Exception
    {
        public Guid CommandId { get; } = commandId;
        public string Fingerprint { get; } = fingerprint;
    }

    private async Task<object> BuildWalletSnapshotAsync(Guid customerId, CancellationToken ct)
        => await GetWalletAsync(customerId, ct);

    public async Task ConfirmPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        _ = await db.PendingWalletTopUps.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "شارژ در انتظار یافت نشد.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """SELECT "Id" FROM pending_wallet_topups WHERE "Id" = {0} FOR UPDATE""",
            pendingId);
        var locked = await db.PendingWalletTopUps.FirstAsync(p => p.Id == pendingId, ct);
        if (locked.Status != PendingWalletTopUpStatus.Pending)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "این شارژ در انتظار تأیید نیست.");
        }

        locked.Status = PendingWalletTopUpStatus.Confirmed;
        var groupId = Guid.NewGuid();
        await WithMemberWriteLockAsync(locked.CustomerId, async (_, member, innerCt) =>
        {
            var before = new { member.RealToman, member.GiftToman };
            var pocket = MemberWalletMapper.ToPocket(member);
            pocket = MemberWalletMapper.ApplyRealTopUp(pocket, locked.AmountToman);
            if (locked.GiftToman > 0)
            {
                pocket = MemberWalletMapper.ApplyGiftTopUp(pocket, locked.GiftToman);
            }

            MemberWalletMapper.ApplyPocket(member, pocket);
            WriteTopUpLedgerPair(
                locked.CustomerId,
                locked.AmountToman,
                locked.GiftToman,
                locked.Method,
                locked.DestinationCardId,
                locked.Reference,
                groupId,
                locked.ShiftId);
            var ledgers = db.ChangeTracker.Entries<WalletLedgerEntryEntity>()
                .Where(e => e.State == EntityState.Added && e.Entity.TopUpGroupId == groupId)
                .Select(e => e.Entity)
                .ToList();
            foreach (var ledger in ledgers)
            {
                await ApplyWalletTopUpOriginatingWorkingDayAsync(ledger, locked.ShiftId, locked.CreatedAtUtc, innerCt);
            }

            await audit.AppendAsync("wallet.topup.confirmed", new { pendingId, before, after = new { member.RealToman, member.GiftToman } }, innerCt);
        }, ct, tx);
        await tx.CommitAsync(ct);
    }

    public async Task RejectPendingTopUpAsync(long pendingId, CancellationToken ct)
    {
        _ = await db.PendingWalletTopUps.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pendingId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "شارژ در انتظار یافت نشد.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.PendingWalletTopUps
            .Where(p => p.Id == pendingId && p.Status == PendingWalletTopUpStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Status, PendingWalletTopUpStatus.Rejected),
                ct);
        if (updated == 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "این شارژ در انتظار تأیید نیست.");
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
        OperatorMoneyValidation.EnsurePositiveBounded(giftToman, "invalid_amount");

        _ = await members.EnsureMemberAsync(customerId, ct);
        var (shiftId, workingDayYear, workingDayMonth, workingDayDay) =
            await ResolveManagerGiftLedgerContextAsync(ct);

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
                ShiftId = shiftId,
                WorkingDayYear = workingDayYear,
                WorkingDayMonth = workingDayMonth,
                WorkingDayDay = workingDayDay,
            });
            await audit.AppendAsync("wallet.gift", new { customerId, giftToman }, innerCt);
        }, ct);
    }

    private async Task<(Guid? ShiftId, int? WorkingDayYear, int? WorkingDayMonth, int? WorkingDayDay)> ResolveManagerGiftLedgerContextAsync(
        CancellationToken ct)
    {
        var settings = await shiftGate.LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return (null, null, null, null);
        }

        var openShift = await shiftGate.GetOpenShiftForUpdateAsync(ct);
        if (openShift is not null)
        {
            return (openShift.Id, null, null, null);
        }

        var lastClose = await db.CloseRecords.AsNoTracking()
            .Where(c => c.ShiftId != null)
            .OrderByDescending(c => c.ClosedAtUtc)
            .FirstOrDefaultAsync(ct);
        if (lastClose is null)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "no_open_shift", "شیفت بازی باز نیست.");
        }

        return (null, lastClose.WorkingDayYear, lastClose.WorkingDayMonth, lastClose.WorkingDayDay);
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
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_gift", "هدیهٔ افتتاحیه نمی‌تواند منفی باشد.");
        }

        if (body.RealToman is long realToman)
        {
            OperatorMoneyValidation.EnsureNonNegativeBounded(realToman, "invalid_opening");
        }

        if (body.GiftToman is long giftToman)
        {
            OperatorMoneyValidation.EnsureNonNegativeBounded(giftToman, "invalid_gift");
        }

        if (body.DebtToman is long debtToman)
        {
            OperatorMoneyValidation.EnsurePositiveBounded(debtToman, "invalid_opening");
        }

        if (body.LifetimeHours is not null && body.LevelName is not null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "ساعت بازی یا سطح را وارد کنید، نه هر دو.");
        }

        if (body.RealToman is > 0 && body.DebtToman is > 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "پول واقعی و بدهی را با هم وارد نکنید.");
        }

        var hasMoneyFields = body.RealToman is not null || body.GiftToman is not null || body.DebtToman is not null;
        if (!hasMoneyFields && body.LifetimeHours is null && body.LevelName is null)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "مبلغ افتتاحیه یا ساعت بازی را وارد کنید.");
        }

        await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
        {
            if (member.HasOpeningBalance)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "opening_exists", "ماندهٔ افتتاحیه قبلاً ثبت شده است.");
            }

            var spendableBefore = member.RealToman + member.GiftToman;
            if (!hasMoneyFields && spendableBefore != 0)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_opening", "مبلغ کیف پول افتتاحیه را وارد کنید.");
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
                    ?? throw new ApiException(StatusCodes.Status400BadRequest, "invalid_level", "سطح ناشناخته است.");
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
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_floor", "سقف بدهی باید صفر یا منفی باشد.");
        }

        if (floorToman < -OperatorMoneyValidation.MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_floor", "مبلغ باید عدد صحیح مثبت باشد.");
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
        if (percent is < 0 or > 100)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_policy", "درصد تولد باید بین ۰ و ۱۰۰ باشد.");
        }

        if (!IsAllowedBirthdayWindowKind(windowKind))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_policy", "نوع بازهٔ تولد نامعتبر است.");
        }

        if (string.Equals(windowKind, "day_window", StringComparison.Ordinal))
        {
            if (windowDays < 1)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_window_days", "تعداد روزهای بازه باید دست‌کم ۱ باشد.");
            }

            if (!IsAllowedBirthdayAnchor(anchor))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "invalid_policy", "نوع حساب روزهای بازه نامعتبر است.");
            }
        }

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
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_cap", "سقف تخفیف ترکیبی باید بین ۰ و ۱۰۰ باشد.");
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
            giftToman = p.GiftToman,
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

    private static bool IsAllowedBirthdayWindowKind(string windowKind) =>
        string.Equals(windowKind, "birth_month", StringComparison.Ordinal)
        || string.Equals(windowKind, "exact_day", StringComparison.Ordinal)
        || string.Equals(windowKind, "day_window", StringComparison.Ordinal);

    private static bool IsAllowedBirthdayAnchor(string anchor) =>
        string.Equals(anchor, "on_and_after", StringComparison.Ordinal)
        || string.Equals(anchor, "around", StringComparison.Ordinal);

    private void EnsureManager()
    {
        if (op.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "فقط مدیر یا مالک می‌تواند این کار را انجام دهد.");
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
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "تخصیص کیف پول انجام نشد.");
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
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "خرج از کیف پول انجام نشد.");
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
        });
    }

    public async Task ReverseInvoiceEffectsAsync(InvoiceEntity invoice, CancellationToken ct)
    {
        var effect = await db.InvoiceMemberEffects.FirstOrDefaultAsync(e => e.InvoiceId == invoice.Id, ct);
        if (effect is null)
        {
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

    }
}
