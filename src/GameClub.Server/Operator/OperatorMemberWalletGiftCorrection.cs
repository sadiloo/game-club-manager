using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public static class WalletGiftCapMath
{
    public static long MaxAllowedGiftToman(long amountToman, long maxToman, int maxPercent)
    {
        if (maxPercent <= 0 || amountToman <= 0)
        {
            return Math.Min(maxToman, 0L);
        }

        var byPercent = MoneyMath.CheckedMultiply(amountToman, maxPercent) / 100;
        return Math.Min(maxToman, byPercent);
    }

    public static void ValidateOperatorGiftOrThrow(
        UserRole role,
        long amountToman,
        long giftToman,
        BranchMemberSettingsEntity settings)
    {
        if (giftToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_gift", "Gift cannot be negative.");
        }

        if (giftToman == 0 || role is UserRole.Manager or UserRole.Owner)
        {
            return;
        }

        var maxGift = MaxAllowedGiftToman(amountToman, settings.OperatorGiftMaxToman, settings.OperatorGiftMaxPercent);
        var giftOk = giftToman <= settings.OperatorGiftMaxToman;
        var percentOk = MoneyMath.CheckedMultiply(giftToman, 100)
            <= MoneyMath.CheckedMultiply(amountToman, settings.OperatorGiftMaxPercent);
        if (!giftOk || !percentOk)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "gift_cap_exceeded",
                $"Gift exceeds operator cap (max {maxGift} toman for this amount).");
        }
    }
}

public sealed partial class OperatorMemberWalletService
{
    public async Task<object> GetOperatorGiftCapAsync(CancellationToken ct)
    {
        var settings = await members.LoadSettingsAsync(ct);
        return new { maxToman = settings.OperatorGiftMaxToman, maxPercent = settings.OperatorGiftMaxPercent };
    }

    public async Task SetOperatorGiftCapAsync(long maxToman, int maxPercent, CancellationToken ct)
    {
        EnsureManager();
        if (maxToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_cap", "Max gift toman must be non-negative.");
        }

        if (maxPercent is < 0 or > 100)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_cap", "Max gift percent must be between 0 and 100.");
        }

        var settings = await db.BranchMemberSettings.FirstAsync(ct);
        var before = new { settings.OperatorGiftMaxToman, settings.OperatorGiftMaxPercent };
        settings.OperatorGiftMaxToman = maxToman;
        settings.OperatorGiftMaxPercent = maxPercent;
        await audit.AppendAsync("settings.operator_gift_cap", new { before, after = new { maxToman, maxPercent } }, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task<Guid?> ResolveOpenShiftIdAsync(CancellationToken ct)
    {
        var settings = await shiftGate.LoadSettingsAsync(ct);
        if (!settings.ShiftsEnabled)
        {
            return null;
        }

        var shift = await shiftGate.GetOpenShiftForUpdateAsync(ct)
            ?? throw new ApiException(StatusCodes.Status409Conflict, "no_open_shift", "No shift is open.");
        if (shift.Status != ShiftStatus.Open)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "shift_closed", "Shift is already closed.");
        }

        return shift.Id;
    }

    private void WriteTopUpLedgerPair(
        Guid customerId,
        long amountToman,
        long giftToman,
        PaymentMethod method,
        string? destinationCardId,
        string? reference,
        Guid topUpGroupId,
        Guid? shiftId)
    {
        db.WalletLedger.Add(new WalletLedgerEntryEntity
        {
            CustomerId = customerId,
            AtUtc = clock.UtcNow.UtcDateTime,
            Kind = WalletLedgerKind.TopUpReal,
            RealDeltaToman = amountToman,
            Method = method,
            DestinationCardId = destinationCardId,
            Reference = reference,
            Label = method.ToString(),
            ActorUserId = op.UserId,
            ShiftId = shiftId,
            TopUpGroupId = topUpGroupId,
        });
        if (giftToman > 0)
        {
            db.WalletLedger.Add(new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = clock.UtcNow.UtcDateTime,
                Kind = WalletLedgerKind.TopUpGift,
                GiftDeltaToman = giftToman,
                Method = method,
                DestinationCardId = destinationCardId,
                Reference = reference,
                Label = "topup_gift",
                ActorUserId = op.UserId ?? Guid.Empty,
                ShiftId = shiftId,
                TopUpGroupId = topUpGroupId,
            });
        }
    }

    public async Task<IReadOnlyList<object>> ListCorrectableTopUpsAsync(Guid customerId, CancellationToken ct)
    {
        var groups = await db.WalletLedger.AsNoTracking()
            .Where(w => w.CustomerId == customerId
                        && w.TopUpGroupId != null
                        && w.Kind == WalletLedgerKind.TopUpReal)
            .Select(w => w.TopUpGroupId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var result = new List<object>();
        foreach (var groupId in groups)
        {
            var rows = await db.WalletLedger.AsNoTracking()
                .Where(w => w.TopUpGroupId == groupId)
                .ToListAsync(ct);
            if (rows.Count == 0)
            {
                continue;
            }

            var real = rows.Where(r => r.Kind is WalletLedgerKind.TopUpReal or WalletLedgerKind.Correction)
                .Sum(r => r.RealDeltaToman);
            var gift = rows.Where(r => r.Kind is WalletLedgerKind.TopUpGift or WalletLedgerKind.Correction)
                .Sum(r => r.GiftDeltaToman);
            var anchor = rows.First(r => r.Kind == WalletLedgerKind.TopUpReal);
            result.Add(new
            {
                topUpGroupId = groupId,
                atUtc = anchor.AtUtc,
                method = anchor.Method?.ToString(),
                currentAmountToman = real,
                currentGiftToman = gift,
                actorUserId = anchor.ActorUserId,
            });
        }

        return result.OrderByDescending(r => ((dynamic)r).atUtc).Cast<object>().ToList();
    }

    public sealed record CorrectTopUpRequest(long NewAmountToman, long NewGiftToman, string Description, Guid? CommandId);

    public async Task<object> CorrectTopUpAsync(Guid topUpGroupId, CorrectTopUpRequest body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Description))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "description_required", "Description is required.");
        }

        if (body.NewAmountToman < 0 || body.NewGiftToman < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_amount", "Amounts must be non-negative.");
        }

        var original = await db.WalletLedger.AsNoTracking()
            .Where(w => w.TopUpGroupId == topUpGroupId && w.Kind == WalletLedgerKind.TopUpReal)
            .FirstOrDefaultAsync(ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "Top-up group not found.");

        if (body.CommandId is Guid cmdId)
        {
            var existing = await db.WalletTopUpCorrections.AsNoTracking()
                .FirstOrDefaultAsync(c => c.TopUpGroupId == topUpGroupId && c.Description == body.Description.Trim(), ct);
            if (existing is not null)
            {
                return new { ok = true, duplicate = true };
            }
        }

        var rows = await db.WalletLedger.AsNoTracking().Where(w => w.TopUpGroupId == topUpGroupId).ToListAsync(ct);
        var currentAmount = rows.Where(r => r.Kind is WalletLedgerKind.TopUpReal or WalletLedgerKind.Correction).Sum(r => r.RealDeltaToman);
        var currentGift = rows.Where(r => r.Kind is WalletLedgerKind.TopUpGift or WalletLedgerKind.Correction).Sum(r => r.GiftDeltaToman);
        if (currentAmount == body.NewAmountToman && currentGift == body.NewGiftToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "no_change", "No change from current amounts.");
        }

        var settings = await members.LoadSettingsAsync(ct);
        WalletGiftCapMath.ValidateOperatorGiftOrThrow(op.Role ?? UserRole.Operator, body.NewAmountToman, body.NewGiftToman, settings);

        var customerId = original.CustomerId;
        var isWindowOpen = await IsTopUpCorrectionWindowOpenAsync(original, settings, ct);
        if (!isWindowOpen && op.Role == UserRole.Operator)
        {
            throw new ApiException(
                StatusCodes.Status409Conflict,
                "correction_window_closed",
                "شیفت این شارژ بسته شده؛ فقط مدیر می‌تواند اصلاحش کند.");
        }

        var realDelta = body.NewAmountToman - currentAmount;
        var giftDelta = body.NewGiftToman - currentGift;
        var postClose = !isWindowOpen;
        Guid? correctionShiftId = original.ShiftId;
        if (postClose)
        {
            correctionShiftId = await ResolveOpenShiftIdAsync(ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await WithMemberWriteLockAsync(customerId, async (_, member, innerCt) =>
        {
            var pocket = MemberWalletMapper.ToPocket(member);
            if (realDelta < 0)
            {
                var projectedReal = MoneyMath.CheckedAdd(member.RealToman, realDelta);
                if (projectedReal < 0)
                {
                    throw new ApiException(
                        StatusCodes.Status409Conflict,
                        "correction_exceeds_balance",
                        $"این اصلاح موجودی پرداختی مشتری را منفی می‌کند (مشتری قبلاً خرج کرده). اصلاح انجام نشد؛ به مدیر خبر دهید. availableRealToman={member.RealToman}");
                }

                pocket = WalletPocketMath.Add(pocket, realDelta, 0);
                pocket = MemberWalletMapper.RefreshSpendBasis(pocket);
            }
            else if (realDelta > 0)
            {
                pocket = MemberWalletMapper.ApplyRealTopUp(pocket, realDelta);
            }

            if (giftDelta > 0)
            {
                pocket = MemberWalletMapper.ApplyGiftTopUp(pocket, giftDelta);
            }
            else if (giftDelta < 0)
            {
                if (MoneyMath.CheckedAdd(member.GiftToman, giftDelta) < 0)
                {
                    throw new ApiException(StatusCodes.Status409Conflict, "gift_already_spent", "Gift already spent.");
                }

                pocket = WalletPocketMath.Add(pocket, 0, giftDelta);
                pocket = MemberWalletMapper.RefreshSpendBasis(pocket);
            }

            MemberWalletMapper.ApplyPocket(member, pocket);
            var ledger = new WalletLedgerEntryEntity
            {
                CustomerId = customerId,
                AtUtc = clock.UtcNow.UtcDateTime,
                Kind = WalletLedgerKind.Correction,
                RealDeltaToman = realDelta,
                GiftDeltaToman = giftDelta,
                Method = original.Method,
                DestinationCardId = original.DestinationCardId,
                Label = "topup_correction",
                ActorUserId = op.UserId ?? Guid.Empty,
                ShiftId = correctionShiftId,
                TopUpGroupId = topUpGroupId,
                IsPostCloseAdjustment = postClose,
            };
            db.WalletLedger.Add(ledger);
            await db.SaveChangesAsync(innerCt);
            db.WalletTopUpCorrections.Add(new WalletTopUpCorrectionEntity
            {
                TopUpGroupId = topUpGroupId,
                CorrectionLedgerEntryId = ledger.Id,
                CustomerId = customerId,
                Method = original.Method ?? PaymentMethod.Cash,
                BeforeAmountToman = currentAmount,
                AfterAmountToman = body.NewAmountToman,
                BeforeGiftToman = currentGift,
                AfterGiftToman = body.NewGiftToman,
                Description = body.Description.Trim(),
                ActorUserId = op.UserId ?? Guid.Empty,
                AtUtc = clock.UtcNow.UtcDateTime,
                ShiftId = correctionShiftId,
                OriginalShiftId = original.ShiftId,
                IsPostCloseAdjustment = postClose,
            });
            await audit.AppendAsync(
                "wallet.topup.correction",
                new
                {
                    topUpGroupId,
                    body.CommandId,
                    beforeAmount = currentAmount,
                    afterAmount = body.NewAmountToman,
                    beforeGift = currentGift,
                    afterGift = body.NewGiftToman,
                    body.Description,
                    postClose,
                },
                innerCt);
        }, ct, tx);
        await tx.CommitAsync(ct);
        return await GetWalletAsync(customerId, ct);
    }

    public async Task<IReadOnlyList<object>> ListTopUpCorrectionsAsync(DateTime? from, DateTime? to, CancellationToken ct)
    {
        EnsureManager();
        var q = db.WalletTopUpCorrections.AsNoTracking().AsQueryable();
        if (from is DateTime f)
        {
            q = q.Where(c => c.AtUtc >= f);
        }

        if (to is DateTime t)
        {
            q = q.Where(c => c.AtUtc <= t);
        }

        var rows = await q.OrderByDescending(c => c.AtUtc).ToListAsync(ct);
        return rows.Select(c => new
        {
            c.Id,
            c.TopUpGroupId,
            c.CustomerId,
            method = c.Method.ToString(),
            c.BeforeAmountToman,
            c.AfterAmountToman,
            c.BeforeGiftToman,
            c.AfterGiftToman,
            c.Description,
            c.ActorUserId,
            c.AtUtc,
            c.ShiftId,
            c.OriginalShiftId,
            c.IsPostCloseAdjustment,
        }).Cast<object>().ToList();
    }

    public static long SumGiftIssuedToman(IEnumerable<WalletLedgerEntryEntity> rows)
    {
        long total = 0;
        foreach (var row in rows)
        {
            if (row.Kind is WalletLedgerKind.TopUpGift or WalletLedgerKind.Correction)
            {
                total = MoneyMath.CheckedAdd(total, row.GiftDeltaToman);
            }
        }

        return total;
    }

    private async Task<bool> IsTopUpCorrectionWindowOpenAsync(
        WalletLedgerEntryEntity original,
        BranchMemberSettingsEntity _,
        CancellationToken ct)
    {
        var shiftSettings = await shiftGate.LoadSettingsAsync(ct);
        if (shiftSettings.ShiftsEnabled)
        {
            if (original.ShiftId is not Guid sid)
            {
                return false;
            }

            var shift = await db.Shifts.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct);
            return shift is not null && shift.Status == ShiftStatus.Open;
        }

        return original.WorkingDayYear is null;
    }
}