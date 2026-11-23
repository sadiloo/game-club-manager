using System.Text.Json;
using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Operator;

public sealed record BillAmounts(
    long UnroundedPayableToman,
    long RoundingToman,
    long CollectibleToman,
    long SurchargeToman,
    long TimeGrossToman,
    long BuffetGrossToman,
    long ServicesGrossToman,
    DateTime BilledToUtc,
    long DiscountToman = 0,
    MemberBillResult? Detail = null);

public sealed class BillCalculator(
    SessionBilling billing,
    RateConfigService rates,
    RoundingSettingsService roundingSettings,
    OperatorMemberContext memberContext)
{
    public async Task<BillAmounts> ComputeAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        IReadOnlyList<OpenSaleLineEntity>? saleLines,
        CancellationToken ct)
        => await ComputeCoreAsync(session, endUtc, surchargeToman, saleLines ?? [], ct);

    public Task<BillAmounts> ComputeAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        CancellationToken ct)
        => ComputeCoreAsync(session, endUtc, surchargeToman, [], ct);

    public Task<BillAmounts> ComputeBuffetOnlyAsync(IReadOnlyList<OpenSaleLineEntity> saleLines, CancellationToken ct)
        => ComputeBuffetOnlyAsync(saleLines, null, null, ct);

    public async Task<BillAmounts> ComputeBuffetOnlyAsync(
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        Guid? customerId,
        IReadOnlyList<Tender>? previewTenders,
        CancellationToken ct,
        IReadOnlyList<OperatorBillService.PaymentInput>? paymentInputs = null)
    {
        var snapshot = await SnapshotForPaymentAsync(ct);
        var settings = await memberContext.LoadSettingsAsync(ct);
        CustomerMemberEntity? member = null;
        if (customerId is Guid id)
        {
            member = await memberContext.EnsureMemberAsync(id, ct);
        }

        var (_, paymentRounding) = await roundingSettings.LoadForPaymentAsync(ct);
        paymentRounding = WalletShortRemainderSupport.AdjustPaymentRoundingForShortCashChoice(paymentRounding, paymentInputs);
        return ToBillAmounts(MemberBillComposer.ComputeBuffetOnly(saleLines, snapshot, customerId, member, settings, previewTenders, paymentRounding));
    }

    public async Task<BillAmounts> ComputeWithTendersAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        IReadOnlyList<Tender> tenders,
        CancellationToken ct,
        IReadOnlyList<OperatorBillService.PaymentInput>? paymentInputs = null)
        => ToBillAmounts(await ComputeMemberAsync(session, endUtc, surchargeToman, saleLines, tenders, ct, paymentInputs));

    private Task<BillAmounts> ComputeCoreAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        CancellationToken ct)
        => ComputeWithTendersAsync(session, endUtc, surchargeToman, saleLines, [], ct);

    private async Task<MemberBillResult> ComputeMemberAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        long surchargeToman,
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        IReadOnlyList<Tender> previewTenders,
        CancellationToken ct,
        IReadOnlyList<OperatorBillService.PaymentInput>? paymentInputs = null)
    {
        var rawSnapshot = session.RateCardSnapshotId != 0
            ? await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : await rates.LoadAsync(ct);
        var snapshot = await roundingSettings.ResolveRatesForSessionBillAsync(rawSnapshot, ct);
        var (_, paymentRounding) = await roundingSettings.LoadForPaymentAsync(ct);
        paymentRounding = WalletShortRemainderSupport.AdjustPaymentRoundingForShortCashChoice(paymentRounding, paymentInputs);
        var charge = billing.BuildCharge(session, endUtc, snapshot);
        var settings = await memberContext.LoadSettingsAsync(ct);
        CustomerMemberEntity? member = null;
        if (session.CustomerId is Guid cid)
        {
            member = await memberContext.EnsureMemberAsync(cid, ct);
        }

        var applyPricing = session.Station?.Group?.Kind != StationKind.Pc || settings.ApplyDiscountsToPc;
        var stationKind = session.Station?.Group?.Kind ?? StationKind.Console;
        return MemberBillComposer.Compute(
            charge,
            snapshot,
            saleLines,
            surchargeToman,
            endUtc,
            session.CustomerId,
            member,
            settings,
            stationKind,
            previewTenders.Count > 0 ? previewTenders : null,
            applyPricing,
            paymentRounding);
    }

    private async Task<BranchRatesSnapshot> SnapshotForPaymentAsync(CancellationToken ct)
    {
        var snapshot = await rates.LoadAsync(ct);
        return await roundingSettings.ResolveRatesForOpenBillAsync(snapshot, ct);
    }

    private static BillAmounts ToBillAmounts(MemberBillResult result)
        => new(
            result.UnroundedPayableToman,
            result.RoundingToman,
            result.CollectibleToman,
            result.SurchargeToman,
            result.TimeGrossToman,
            result.BuffetGrossToman,
            result.ServicesGrossToman,
            result.BilledToUtc,
            result.DiscountToman,
            result);
}

public sealed class OperatorBillService(
    AppDbContext db,
    IClock clock,
    AuditService audit,
    BillCalculator calculator,
    SessionBilling billing,
    SessionCloser closer,
    OperatorPolicies policies,
    OperatorMemberContext memberContext,
    OperatorShiftGate shiftGate,
    RateConfigService rates)
{
    private static long DisplayHourlyToman(
        PlaySegmentEntity segment,
        SessionCharge charge,
        BranchRatesSnapshot snapshot)
    {
        var direct = charge.Lines.FirstOrDefault(l => l.SegmentId == segment.SegmentKey);
        if (direct is not null)
        {
            return direct.HourlyRateToman;
        }

        if (segment.GameTitleId is not null && segment.GameHourlyRateToman is long gameRate and > 0)
        {
            return gameRate;
        }

        var sameGroup = charge.Lines.FirstOrDefault(l => l.GroupId == segment.GroupId);
        if (sameGroup is not null)
        {
            return sameGroup.HourlyRateToman;
        }

        var wall = TimeMapping.ToTehran(segment.StartUtc);
        return RateResolver.Resolve(
            snapshot.Card,
            SpecialDayCalendar.Empty,
            wall,
            segment.GroupId,
            segment.Mode,
            segment.PlayerCount).HourlyRateToman;
    }

    private static bool UsesWallet(IReadOnlyList<PaymentInput> payments)
        => payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);

    public async Task LockMemberWalletRowIfNeededAsync(Guid? customerId, IReadOnlyList<PaymentInput> payments, CancellationToken ct)
    {
        if (customerId is not Guid id || !UsesWallet(payments))
        {
            return;
        }

        await memberContext.ReloadMemberForUpdateAsync(id, ct);
    }
    private static List<Tender> ToTenders(IReadOnlyList<PaymentInput> payments, Guid? customerId)
        => payments.Select(p =>
        {
            var method = OperatorPaymentMethods.Parse(p.Method);
            var memberId = method == PaymentMethod.Wallet ? customerId?.ToString() : null;
            return new Tender(method, p.AmountToman, memberId);
        }).ToList();

    public static object ToWalletShortRemainderDto(WalletShortRemainderSnapshot snapshot)
        => new
        {
            applies = snapshot.Applies,
            remainderToman = snapshot.RemainderToman,
            walletAutoDrawToman = snapshot.WalletAutoDrawToman,
            debtRemainderAllowed = snapshot.DebtRemainderAllowed,
        };

    public async Task<WalletShortRemainderSnapshot> GetWalletShortRemainderSnapshotAsync(
        Guid? customerId,
        long collectibleToman,
        StationKind? stationKind,
        CancellationToken ct)
    {
        var settings = await memberContext.LoadSettingsAsync(ct);
        var rounding = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        CustomerMemberEntity? member = null;
        if (customerId is Guid id)
        {
            member = await db.CustomerMembers.AsNoTracking().FirstOrDefaultAsync(m => m.CustomerId == id, ct);
        }

        return WalletShortRemainderSupport.Compute(collectibleToman, member, settings, rounding, stationKind);
    }

    public async Task<IReadOnlyList<PaymentInput>> PreparePaymentsForAllocationAsync(
        Guid? customerId,
        long collectibleToman,
        StationKind? stationKind,
        IReadOnlyList<PaymentInput> payments,
        CancellationToken ct)
    {
        var rounding = await db.BranchRounding.AsNoTracking().SingleAsync(ct);
        var snapshot = await GetWalletShortRemainderSnapshotAsync(customerId, collectibleToman, stationKind, ct);
        var wallet = payments.FirstOrDefault(p => p.Method.Equals("Wallet", StringComparison.OrdinalIgnoreCase));
        WalletShortRemainderSupport.ValidateWalletShortBalancePolicy(
            snapshot,
            rounding.AllowWalletShortBalanceDebt,
            wallet);
        return WalletShortRemainderSupport.NormalizeWalletShortPayments(payments, snapshot, collectibleToman);
    }

    public void ValidatePayments(
        long collectibleToman,
        IReadOnlyList<PaymentInput> payments,
        MemberBillResult? detail = null,
        Guid? customerId = null)
    {
        foreach (var payment in payments)
        {
            OperatorMoneyValidation.EnsureNonNegativeBounded(payment.AmountToman, "payment_invalid");
        }

        if (collectibleToman == 0)
        {
            if (payments.Count > 0 && payments.Any(p => p.AmountToman > 0))
            {
                var positiveTenders = payments
                    .Select(p => new Tender(OperatorPaymentMethods.Parse(p.Method), p.AmountToman))
                    .ToList();
                var positiveSplit = PaymentSplitter.Split(0, positiveTenders);
                if (!positiveSplit.IsValid)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", OperatorPaymentErrors.Localize(positiveSplit.Errors.FirstOrDefault()));
                }

                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "دست‌کم یک خط پرداخت مثبت لازم است.");
            }

            return;
        }

        if (payments.Count == 0 || payments.All(p => p.AmountToman <= 0))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "دست‌کم یک خط پرداخت مثبت لازم است.");
        }

        var tenders = ToTenders(payments, customerId);
        if (detail?.Allocation is { IsValid: false } allocation)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                OperatorPaymentErrors.Localize(allocation.Errors.FirstOrDefault()));
        }

        if (payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet))
        {
            if (customerId is null)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "wallet_requires_member", "پرداخت کیف پول به عضو پیوسته نیاز دارد.");
            }

            if (detail is null || detail.Allocation is not { IsValid: true } alloc)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "تخصیص کیف پول انجام نشد.");
            }

            var surchargeAlreadyInCollect = detail is { SurchargeToman: > 0 }
                && detail.InvoicePreview.Lines.Any(l =>
                    l.Kind == InvoiceLineKind.Service && l.AmountToman == detail.SurchargeToman);
            var allocPayable = MoneyMath.CheckedAdd(
                alloc.PayableToman,
                surchargeAlreadyInCollect ? 0 : detail?.SurchargeToman ?? 0);
            if (allocPayable != collectibleToman)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "جمع قابل پرداخت با تخصیص هم‌خوان نیست.");
            }

            var tendered = payments.Sum(p => p.AmountToman);
            if (tendered != allocPayable)
            {
                var unrounded = detail!.UnroundedPayableToman;
                var walletOnly = payments.All(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
                var singleWallet = walletOnly && payments.Count == 1;
                var typedUnroundedFullPay = singleWallet
                    && tendered == unrounded
                    && allocPayable == collectibleToman
                    && allocPayable > unrounded;
                if (!typedUnroundedFullPay)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "جمع پرداختی با مبلغ قابل پرداخت برابر نیست.");
                }
            }

            return;
        }

        foreach (var payment in payments)
        {
            var method = OperatorPaymentMethods.Parse(payment.Method);
            if (method is PaymentMethod.Wallet or PaymentMethod.PcBalance)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", "روش پرداخت پشتیبانی نمی‌شود.");
            }
        }

        var split = PaymentSplitter.Split(collectibleToman, tenders);
        if (!split.IsValid)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "payment_invalid", OperatorPaymentErrors.Localize(split.Errors.FirstOrDefault()));
        }
    }

    private async Task<IReadOnlyList<OpenSaleLineEntity>> LoadSaleLinesAsync(Guid sessionId, CancellationToken ct)
    {
        var sale = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .FirstOrDefaultAsync(o => o.SessionId == sessionId, ct);
        return sale?.Lines ?? [];
    }

    private async Task ApplyAmountsToBillAsync(PlaySessionEntity session, PendingBillEntity bill, CancellationToken ct)
    {
        var saleLines = await LoadSaleLinesAsync(session.Id, ct);
        var amounts = await calculator.ComputeAsync(session, bill.FrozenAtUtc, bill.SurchargeToman, saleLines, ct);
        bill.UnroundedPayableToman = amounts.UnroundedPayableToman;
        bill.RoundingToman = amounts.RoundingToman;
        bill.CollectibleToman = amounts.CollectibleToman;
        bill.TimeGrossToman = amounts.TimeGrossToman;
        bill.BuffetGrossToman = amounts.BuffetGrossToman;
        bill.ServicesGrossToman = amounts.ServicesGrossToman;
    }

    public sealed record PaymentInput(
        string Method,
        long AmountToman,
        string? DestinationCardId = null,
        string? Reference = null,
        string? ShortRemainderMethod = null);

    public async Task<PendingBillEntity> OpenConsoleBillAsync(Guid sessionId, CancellationToken ct)
    {
        return await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            if (session.Station!.Group!.Kind != StationKind.Console)
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "not_console", "صدور صورتحساب فقط برای کنسول است.");
            }

            if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "این جلسه نمی‌تواند صورتحساب باز کند.");
            }

            if (session.PendingBill is not null)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "bill_open", "صورتحساب از قبل باز است.");
            }

            var frozenAt = clock.UtcNow.UtcDateTime;
            var revert = CaptureRevertState(session);
            ApplyPlayEnd(session, frozenAt);
            var saleLines = await LoadSaleLinesAsync(session.Id, innerCt);
            var amounts = await calculator.ComputeAsync(session, frozenAt, 0, saleLines, innerCt);
            var bill = new PendingBillEntity
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                FrozenAtUtc = frozenAt,
                OpenedAtUtc = frozenAt,
                UnroundedPayableToman = amounts.UnroundedPayableToman,
                RoundingToman = amounts.RoundingToman,
                CollectibleToman = amounts.CollectibleToman,
                TimeGrossToman = amounts.TimeGrossToman,
                BuffetGrossToman = amounts.BuffetGrossToman,
                ServicesGrossToman = amounts.ServicesGrossToman,
                Source = "console_bill",
                RevertStateJson = JsonSerializer.Serialize(revert),
            };
            session.Status = SessionStatus.BillOpen;
            session.PendingBill = bill;
            db.PendingBills.Add(bill);
            await audit.AppendAsync("bill.open", new { sessionId, frozenAt, revert }, innerCt);
            return bill;
        }, ct);
    }

    public async Task CancelConsoleBillAsync(Guid sessionId, CancellationToken ct)
    {
        await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            var isPc = session.Station?.Group?.Kind == StationKind.Pc;
            if (isPc && session.Status == SessionStatus.AwaitingPayment)
            {
                // L7: dismiss payment UI; station stays occupied until confirm — no resume/unlock.
                await audit.AppendAsync("bill.cancel", new { sessionId, pcAwaitingPayment = true }, innerCt);
                return;
            }

            if (session.Status != SessionStatus.BillOpen || session.PendingBill is null)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "صورتحساب بازی برای لغو نیست.");
            }

            var bill = session.PendingBill;
            db.PendingBills.Remove(bill);
            session.PendingBill = null;
            RestoreRevertState(session, bill);
            await audit.AppendAsync("bill.cancel", new { sessionId }, innerCt);
        }, ct);
    }

    public async Task AddSurchargeAsync(Guid sessionId, string? reason, CancellationToken ct)
    {
        await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            var bill = session.PendingBill
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "صورتحساب بازی برای لغو نیست.");
            bill.SurchargeToman = MoneyMath.CheckedAdd(bill.SurchargeToman, 5_000);
            await ApplyAmountsToBillAsync(session, bill, innerCt);
            await audit.AppendAsync("bill.surcharge", new { sessionId, reason, bill.SurchargeToman }, innerCt);
        }, ct);
    }

    public async Task<InvoiceEntity> SettleSessionDuringCloseAsync(Guid sessionId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE""",
            sessionId);
        var session = await db.Sessions
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .Include(s => s.PendingBill)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
        if (session.Status is SessionStatus.Open or SessionStatus.Paused)
        {
            await OpenBillForCloseSettleAsync(session, ct);
        }

        var bill = session.PendingBill
            ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "برای این جلسه صورتحسابی نیست.");
        if (session.Status is not SessionStatus.BillOpen and not SessionStatus.AwaitingPayment)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "جلسه در انتظار تسویه نیست.");
        }

        var saleLines = await LoadSaleLinesAsync(session.Id, ct);
        var amounts = await calculator.ComputeAsync(session, bill.FrozenAtUtc, bill.SurchargeToman, saleLines, ct);
        var payments = new List<PaymentInput> { new("Cash", amounts.CollectibleToman, null, null) };
        var invoice = await ConfirmBillCoreAsync(session, bill, payments, amounts, saleLines, ct);
        await audit.AppendAsync("bill.confirm", new { sessionId, invoiceNumber = invoice.Number, collectible = invoice.CollectibleToman, atClose = true }, ct);
        return invoice;
    }

    public async Task<object> PreviewCloseSettleCollectibleAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .Include(s => s.Segments)
            .Include(s => s.Pauses)
            .Include(s => s.PendingBill)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
        if (session.Status == SessionStatus.Closed)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "جلسه قبلاً بسته شده است.");
        }

        if (session.PendingBill is PendingBillEntity existing)
        {
            return new { collectibleToman = existing.CollectibleToman };
        }

        if (session.Status is not SessionStatus.Open and not SessionStatus.Paused)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "برای این جلسه صورتحسابی نیست.");
        }

        var endUtc = session.EndedAtUtc ?? clock.UtcNow.UtcDateTime;
        foreach (var seg in session.Segments.Where(s => s.EndUtc is null))
        {
            seg.EndUtc = endUtc;
        }

        var saleLines = await LoadSaleLinesAsync(sessionId, ct);
        var amounts = await calculator.ComputeAsync(session, endUtc, 0, saleLines, ct);
        return new { collectibleToman = amounts.CollectibleToman };
    }

    private async Task OpenBillForCloseSettleAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.PendingBill is not null)
        {
            return;
        }

        var endUtc = session.EndedAtUtc ?? clock.UtcNow.UtcDateTime;
        if (session.EndedAtUtc is null)
        {
            session.EndedAtUtc = endUtc;
            foreach (var seg in session.Segments.Where(s => s.EndUtc is null))
            {
                seg.EndUtc = endUtc;
            }
        }

        if (session.Station!.Group!.Kind == StationKind.Console)
        {
            var frozenAt = endUtc;
            var revert = CaptureRevertState(session);
            ApplyPlayEnd(session, frozenAt);
            var saleLines = await LoadSaleLinesAsync(session.Id, ct);
            var amounts = await calculator.ComputeAsync(session, frozenAt, 0, saleLines, ct);
            var bill = new PendingBillEntity
            {
                Id = Guid.NewGuid(),
                SessionId = session.Id,
                FrozenAtUtc = frozenAt,
                OpenedAtUtc = frozenAt,
                UnroundedPayableToman = amounts.UnroundedPayableToman,
                RoundingToman = amounts.RoundingToman,
                CollectibleToman = amounts.CollectibleToman,
                TimeGrossToman = amounts.TimeGrossToman,
                BuffetGrossToman = amounts.BuffetGrossToman,
                ServicesGrossToman = amounts.ServicesGrossToman,
                Source = "close_settle",
                RevertStateJson = JsonSerializer.Serialize(revert),
            };
            session.Status = SessionStatus.BillOpen;
            session.PendingBill = bill;
            db.PendingBills.Add(bill);
        }
        else
        {
            _ = await CreatePcPendingBillAsync(session, endUtc, "close_settle", "close", ct);
            session.Status = SessionStatus.BillOpen;
        }
    }

    private async Task<InvoiceEntity> ConfirmBillCoreAsync(
        PlaySessionEntity session,
        PendingBillEntity bill,
        IReadOnlyList<PaymentInput> payments,
        BillAmounts amounts,
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        CancellationToken ct)
    {
        ValidatePayments(amounts.CollectibleToman, payments, amounts.Detail, session.CustomerId);
        return await closer.FinalizeFromPendingAsync(session, bill, payments, amounts, ct);
    }

    public async Task<InvoiceEntity> ConfirmBillAsync(Guid sessionId, IReadOnlyList<PaymentInput> payments, CancellationToken ct)
    {
        return await WithSessionBillLockAsync(sessionId, async (session, innerCt) =>
        {
            var bill = session.PendingBill
                ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "صورتحسابی در انتظار تأیید نیست.");
            if (session.Status is not SessionStatus.BillOpen and not SessionStatus.AwaitingPayment)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "invalid_state", "جلسه در انتظار پرداخت نیست.");
            }

            var saleLines = await LoadSaleLinesAsync(session.Id, innerCt);
            var stationKind = session.Station?.Group?.Kind;
            var effective = await PreparePaymentsForAllocationAsync(
                session.CustomerId,
                bill.CollectibleToman,
                stationKind,
                payments,
                innerCt);
            await LockMemberWalletRowIfNeededAsync(session.CustomerId, effective, innerCt);
            var tenders = ToTenders(effective, session.CustomerId);
            var amounts = await calculator.ComputeWithTendersAsync(
                session,
                bill.FrozenAtUtc,
                bill.SurchargeToman,
                saleLines,
                tenders,
                innerCt,
                effective);
            ValidatePayments(amounts.CollectibleToman, effective, amounts.Detail, session.CustomerId);

            var invoice = await closer.FinalizeFromPendingAsync(session, bill, effective, amounts, innerCt);
            await audit.AppendAsync("bill.confirm", new { sessionId, invoiceNumber = invoice.Number, collectible = invoice.CollectibleToman }, innerCt);
            return invoice;
        }, ct);
    }

    public async Task<PendingBillEntity> CreatePcPendingBillAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        string source,
        string? stopReason,
        CancellationToken ct)
    {
        var existing = session.PendingBill
            ?? await db.PendingBills.FirstOrDefaultAsync(b => b.SessionId == session.Id, ct);
        if (existing is not null)
        {
            session.PendingBill = existing;
            session.Status = SessionStatus.AwaitingPayment;
            return existing;
        }

        ApplyPlayEnd(session, endUtc);
        var saleLines = await LoadSaleLinesAsync(session.Id, ct);
        var amounts = await calculator.ComputeAsync(session, endUtc, 0, saleLines, ct);
        if (amounts.Detail?.OvershootAlert is { } alert)
        {
            await audit.AppendAsync("pc_overshoot", new { sessionId = session.Id, alert.Code, alert.AmountToman, alert.Message }, ct);
        }

        var bill = new PendingBillEntity
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            FrozenAtUtc = endUtc,
            OpenedAtUtc = clock.UtcNow.UtcDateTime,
            UnroundedPayableToman = amounts.UnroundedPayableToman,
            RoundingToman = amounts.RoundingToman,
            CollectibleToman = amounts.CollectibleToman,
            TimeGrossToman = amounts.TimeGrossToman,
            BuffetGrossToman = amounts.BuffetGrossToman,
            ServicesGrossToman = amounts.ServicesGrossToman,
            Source = source,
            StopReason = stopReason,
        };
        session.Status = SessionStatus.AwaitingPayment;
        session.PendingBill = bill;
        db.PendingBills.Add(bill);
        return bill;
    }

    public async Task<PendingBillEntity> CreateMachineStopPendingBillAsync(
        PlaySessionEntity session,
        DateTime endUtc,
        CancellationToken ct)
    {
        ApplyPlayEnd(session, endUtc);
        var saleLines = await LoadSaleLinesAsync(session.Id, ct);
        var amounts = await calculator.ComputeAsync(session, endUtc, 0, saleLines, ct);
        var bill = new PendingBillEntity
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            FrozenAtUtc = endUtc,
            OpenedAtUtc = clock.UtcNow.UtcDateTime,
            UnroundedPayableToman = amounts.UnroundedPayableToman,
            RoundingToman = amounts.RoundingToman,
            CollectibleToman = amounts.CollectibleToman,
            TimeGrossToman = amounts.TimeGrossToman,
            BuffetGrossToman = amounts.BuffetGrossToman,
            ServicesGrossToman = amounts.ServicesGrossToman,
            Source = "console_stop",
            StopReason = "stop",
        };
        session.Status = SessionStatus.AwaitingPayment;
        session.PendingBill = bill;
        db.PendingBills.Add(bill);
        return bill;
    }

    public async Task<object> PreviewBillAsync(
        Guid sessionId,
        IReadOnlyList<PaymentInput> payments,
        CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Segments).ThenInclude(seg => seg.GameTitle)
            .Include(s => s.PendingBill)
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
        var bill = session.PendingBill
            ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "برای این جلسه صورتحسابی نیست.");
        var saleLines = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .Where(o => o.SessionId == sessionId)
            .SelectMany(o => o.Lines)
            .ToListAsync(ct);
        var stationKind = session.Station?.Group?.Kind;
        var effective = await PreparePaymentsForAllocationAsync(
            session.CustomerId,
            bill.CollectibleToman,
            stationKind,
            payments,
            ct);
        var tenders = ToTenders(effective, session.CustomerId);
        var amounts = await calculator.ComputeWithTendersAsync(
            session,
            bill.FrozenAtUtc,
            bill.SurchargeToman,
            saleLines,
            tenders,
            ct,
            effective);
        var walletShort = await GetWalletShortRemainderSnapshotAsync(
            session.CustomerId,
            amounts.CollectibleToman,
            stationKind,
            ct);
        var charge = await billing.BuildChargeAsync(session, bill.FrozenAtUtc, ct);
        var snapshot = session.RateCardSnapshotId != 0
            ? await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : await rates.LoadAsync(ct);
        var segments = session.Segments
            .OrderBy(s => s.StartUtc)
            .Select(s =>
            {
                var hourly = DisplayHourlyToman(s, charge, snapshot);
                return new
                {
                    s.SegmentKey,
                    s.PlayerCount,
                    mode = s.Mode.ToString(),
                    hourlyToman = hourly,
                    gameTitleName = s.GameTitle?.Name,
                    displayLine = Ss14BillDisplay.FormatBillSegment(s, hourly, charge),
                    startUtc = s.StartUtc,
                    endUtc = s.EndUtc,
                };
            })
            .ToList();
        var sale = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .FirstOrDefaultAsync(o => o.SessionId == sessionId, ct);
        var (levelDiscountToman, birthdayDiscountToman) = SplitMemberDiscounts(amounts.Detail);
        var writeOffToman = amounts.Detail?.InvoicePreview.Lines
            .Where(l => l.Kind == InvoiceLineKind.WriteOff)
            .Sum(l => -l.AmountToman) ?? 0L;
        var overshootAlert = amounts.Detail?.OvershootAlert is { } alert
            ? new { alert.Code, alert.AmountToman, alert.Message }
            : null;
        return new
        {
            bill.SessionId,
            playerCount = session.PlayerCount,
            groupId = session.Station!.GroupId,
            unroundedPayableToman = amounts.UnroundedPayableToman,
            roundingToman = amounts.RoundingToman,
            collectibleToman = amounts.CollectibleToman,
            surchargeToman = bill.SurchargeToman,
            timeGrossToman = amounts.TimeGrossToman,
            buffetGrossToman = amounts.BuffetGrossToman,
            servicesGrossToman = amounts.ServicesGrossToman,
            levelDiscountToman,
            birthdayDiscountToman,
            writeOffToman,
            overshootAlert,
            saleLines = sale is null ? Array.Empty<SaleLineDto>() : OpenSaleMapper.ToDtos(sale.Lines),
            frozenAtUtc = bill.FrozenAtUtc,
            source = bill.Source,
            stopReason = bill.StopReason,
            segments,
            linkedCustomer = CustomerMapper.ToLinkedChip(session.Customer),
            walletShortRemainder = ToWalletShortRemainderDto(walletShort),
        };
    }

    public async Task<object> GetBillDetailAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking()
            .Include(s => s.Customer)
            .Include(s => s.Segments).ThenInclude(seg => seg.GameTitle)
            .Include(s => s.PendingBill)
            .Include(s => s.Station)!.ThenInclude(st => st!.Group)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
        var bill = session.PendingBill
            ?? throw new ApiException(StatusCodes.Status409Conflict, "no_bill", "برای این جلسه صورتحسابی نیست.");
        var charge = await billing.BuildChargeAsync(session, bill.FrozenAtUtc, ct);
        var snapshot = session.RateCardSnapshotId != 0
            ? await rates.LoadSnapshotAsync(session.RateCardSnapshotId, ct)
            : await rates.LoadAsync(ct);
        var segments = session.Segments
            .OrderBy(s => s.StartUtc)
            .Select(s =>
            {
                var hourly = DisplayHourlyToman(s, charge, snapshot);
                return new
                {
                    s.SegmentKey,
                    s.PlayerCount,
                    mode = s.Mode.ToString(),
                    hourlyToman = hourly,
                    gameTitleName = s.GameTitle?.Name,
                    displayLine = Ss14BillDisplay.FormatBillSegment(s, hourly, charge),
                    startUtc = s.StartUtc,
                    endUtc = s.EndUtc,
                };
            })
            .ToList();
        var sale = await db.OpenSales.AsNoTracking()
            .Include(o => o.Lines).ThenInclude(l => l.CatalogItem)
            .FirstOrDefaultAsync(o => o.SessionId == sessionId, ct);
        var saleLines = sale?.Lines.ToList() ?? [];
        var amounts = await calculator.ComputeAsync(session, bill.FrozenAtUtc, bill.SurchargeToman, saleLines, ct);
        var (levelDiscountToman, birthdayDiscountToman) = SplitMemberDiscounts(amounts.Detail);
        var writeOffToman = amounts.Detail?.InvoicePreview.Lines
            .Where(l => l.Kind == InvoiceLineKind.WriteOff)
            .Sum(l => -l.AmountToman) ?? 0L;
        var overshootAlert = amounts.Detail?.OvershootAlert is { } alert
            ? new { alert.Code, alert.AmountToman, alert.Message }
            : null;
        return new
        {
            bill.SessionId,
            playerCount = session.PlayerCount,
            groupId = session.Station!.GroupId,
            unroundedPayableToman = bill.UnroundedPayableToman,
            roundingToman = bill.RoundingToman,
            collectibleToman = bill.CollectibleToman,
            surchargeToman = bill.SurchargeToman,
            timeGrossToman = bill.TimeGrossToman,
            buffetGrossToman = bill.BuffetGrossToman,
            servicesGrossToman = bill.ServicesGrossToman,
            levelDiscountToman,
            birthdayDiscountToman,
            writeOffToman,
            overshootAlert,
            saleLines = sale is null ? Array.Empty<SaleLineDto>() : OpenSaleMapper.ToDtos(sale.Lines),
            frozenAtUtc = bill.FrozenAtUtc,
            source = bill.Source,
            stopReason = bill.StopReason,
            segments,
            linkedCustomer = CustomerMapper.ToLinkedChip(session.Customer),
            walletShortRemainder = ToWalletShortRemainderDto(await GetWalletShortRemainderSnapshotAsync(
                session.CustomerId,
                bill.CollectibleToman,
                session.Station?.Group?.Kind,
                ct)),
        };
    }

    public async Task<PendingBillEntity?> GetPendingAsync(Guid sessionId, CancellationToken ct)
        => await db.PendingBills.AsNoTracking().FirstOrDefaultAsync(b => b.SessionId == sessionId, ct);

    private static void ApplyPlayEnd(PlaySessionEntity session, DateTime endUtc)
    {
        foreach (var seg in session.Segments.Where(s => s.EndUtc is null))
        {
            seg.EndUtc = endUtc;
        }

        foreach (var pause in session.Pauses.Where(p => p.EndUtc is null))
        {
            pause.EndUtc = endUtc;
        }

        session.EndedAtUtc = endUtc;
    }

    private sealed record RevertSnapshot(string Status, List<SegmentRevert> Segments, List<PauseRevert> Pauses);

    private sealed record SegmentRevert(long Id, DateTime? EndUtc);

    private sealed record PauseRevert(long Id, DateTime? EndUtc);

    private static RevertSnapshot CaptureRevertState(PlaySessionEntity session)
        => new(
            session.Status.ToString(),
            session.Segments.Select(s => new SegmentRevert(s.Id, s.EndUtc)).ToList(),
            session.Pauses.Select(p => new PauseRevert(p.Id, p.EndUtc)).ToList());

    private static void RestoreRevertState(PlaySessionEntity session, PendingBillEntity bill)
    {
        if (string.IsNullOrWhiteSpace(bill.RevertStateJson))
        {
            return;
        }

        var revert = JsonSerializer.Deserialize<RevertSnapshot>(bill.RevertStateJson)
            ?? throw new ApiException(StatusCodes.Status500InternalServerError, "revert_missing", "وضعیت بازگشت صورتحساب موجود نیست.");
        session.Status = Enum.Parse<SessionStatus>(revert.Status);
        session.EndedAtUtc = null;
        foreach (var seg in revert.Segments)
        {
            session.Segments.First(s => s.Id == seg.Id).EndUtc = seg.EndUtc;
        }

        foreach (var pause in revert.Pauses)
        {
            session.Pauses.First(p => p.Id == pause.Id).EndUtc = pause.EndUtc;
        }
    }

    private async Task<T> WithSessionBillLockAsync<T>(
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
                await shiftGate.EnsureOpenShiftForMoneyAsync(ct);
                await db.Database.ExecuteSqlRawAsync(
                    """SELECT "Id" FROM sessions WHERE "Id" = {0} FOR UPDATE NOWAIT""",
                    sessionId);
            }
            catch (Exception ex) when (PostgresErrors.IsLockNotAvailable(ex) || PostgresErrors.IsDeadlock(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "جلسه در حال به‌روزرسانی است.");
            }

            var session = await db.Sessions
                .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                .Include(s => s.Segments)
                .Include(s => s.Pauses)
                .Include(s => s.PendingBill)
                .FirstOrDefaultAsync(s => s.Id == sessionId, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "جلسه یافت نشد.");
            try
            {
                var result = await action(session, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (policies.ConcurrentBillActionReturns409)
            {
                throw new ApiException(StatusCodes.Status409Conflict, "stale_bill", "صورتحساب جای دیگر به‌روز شده؛ تازه‌سازی کنید و دوباره بزنید.");
            }
            catch (Exception ex) when (PostgresErrors.IsDeadlock(ex))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "session_busy", "جلسه در حال به‌روزرسانی است.");
            }
        });
    }

    private Task WithSessionBillLockAsync(
        Guid sessionId,
        Func<PlaySessionEntity, CancellationToken, Task> action,
        CancellationToken ct)
        => WithSessionBillLockAsync(sessionId, async (s, inner) =>
        {
            await action(s, inner);
            return false;
        }, ct);

    private static (long Level, long Birthday) SplitMemberDiscounts(MemberBillResult? detail)
    {
        if (detail?.InvoicePreview is not { } preview)
        {
            return (0, 0);
        }

        long level = 0;
        long birthday = 0;
        foreach (var line in preview.Lines.Where(l => l.Kind == InvoiceLineKind.Discount))
        {
            var amount = -line.AmountToman;
            if (line.Description.Contains("Birthday", StringComparison.OrdinalIgnoreCase))
            {
                birthday = MoneyMath.CheckedAdd(birthday, amount);
            }
            else
            {
                level = MoneyMath.CheckedAdd(level, amount);
            }
        }

        return (level, birthday);
    }
}

public sealed class OperatorGridService(AppDbContext db, SessionService sessions)
{
    public async Task<IReadOnlyList<object>> GetGridAsync(CancellationToken ct)
    {
        var stations = await db.Stations.AsNoTracking()
            .Include(s => s.Group)
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .ToListAsync(ct);
        var openSessions = await db.Sessions.AsNoTracking()
            .Include(s => s.PendingBill)
            .Include(s => s.Customer)
            .Include(s => s.CurrentGameTitle)
            .Where(s => s.Status != SessionStatus.Closed)
            .ToListAsync(ct);
        var byStation = openSessions
            .GroupBy(s => s.StationId)
            .ToDictionary(g => g.Key, g => PickTileSession(g.ToList()));

        var tiles = new List<object>();
        foreach (var station in stations)
        {
            if (!byStation.TryGetValue(station.Id, out var session))
            {
                var idleStatus = station.OperationalState switch
                {
                    StationOperationalState.Broken => "Broken",
                    StationOperationalState.Reserved => "Reserved",
                    _ => "Free",
                };
                tiles.Add(new
                {
                    station.Id,
                    station.Name,
                    station.GroupId,
                    groupKind = station.Group?.Kind.ToString(),
                    station.DefaultPlayerCount,
                    mode = station.DefaultPlayMode.ToString(),
                    defaultPlayMode = station.DefaultPlayMode.ToString(),
                    operationalState = station.OperationalState.ToString(),
                    status = idleStatus,
                    amountToman = (long?)null,
                    sessionId = (Guid?)null,
                    currentGameTitleId = (int?)null,
                    currentGameTitleName = (string?)null,
                });
                continue;
            }

            var status = MapStatus(session);
            long? amount = null;
            if (status is "Playing" or "Paused")
            {
                var loaded = await db.Sessions
                    .Include(s => s.Station)!.ThenInclude(st => st!.Group)
                    .Include(s => s.Segments)
                    .Include(s => s.Pauses)
                    .FirstAsync(s => s.Id == session.Id, ct);
                amount = await sessions.LiveAmountAsync(loaded, ct);
            }
            else if (session.PendingBill is not null)
            {
                amount = session.PendingBill.CollectibleToman;
            }

            Guid? openSaleId = null;
            if (status is "Playing" or "Paused")
            {
                openSaleId = await db.OpenSales.AsNoTracking()
                    .Where(o => o.SessionId == session.Id)
                    .Select(o => (Guid?)o.Id)
                    .FirstOrDefaultAsync(ct);
            }

            tiles.Add(new
            {
                station.Id,
                station.Name,
                station.GroupId,
                groupKind = station.Group?.Kind.ToString(),
                station.DefaultPlayerCount,
                mode = session.Mode.ToString(),
                defaultPlayMode = station.DefaultPlayMode.ToString(),
                playMode = session.Mode.ToString(),
                playerCount = session.PlayerCount,
                operationalState = station.OperationalState.ToString(),
                status,
                amountToman = amount,
                sessionId = session.Id,
                rowVersion = session.RowVersion,
                openSaleId,
                linkedCustomer = CustomerMapper.ToLinkedChip(session.Customer),
                currentGameTitleId = session.CurrentGameTitleId,
                currentGameTitleName = session.CurrentGameTitle?.Name,
                currentGameRatedPlayerCounts = await ResolveGameRatedPlayerCountsAsync(session, ct),
            });
        }

        tiles.Add(new
        {
            id = BuffetSaleConstants.VirtualStationId,
            name = "بوفه",
            groupId = BuffetSaleConstants.VirtualGroupId,
            defaultPlayerCount = 0,
            mode = "BuffetOnly",
            status = "Buffet only",
            amountToman = (long?)null,
            sessionId = (Guid?)null,
            openSaleId = (Guid?)null,
        });

        return tiles;
    }

    private async Task<int[]?> ResolveGameRatedPlayerCountsAsync(PlaySessionEntity session, CancellationToken ct)
    {
        if (session.CurrentGameTitleId is not int gameId)
        {
            return null;
        }

        var mode = session.Mode.ToString();
        var lines = await db.GameRateLines.AsNoTracking()
            .Where(r => r.GameTitleId == gameId && r.PlayMode == mode && r.HourlyToman > 0)
            .ToListAsync(ct);
        if (lines.Count == 0)
        {
            return Array.Empty<int>();
        }

        if (session.Mode == PlayMode.Online)
        {
            return [session.PlayerCount];
        }

        return lines
            .Where(r => r.PlayerCount is int)
            .Select(r => r.PlayerCount!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
    }

    private static PlaySessionEntity PickTileSession(IReadOnlyList<PlaySessionEntity> onStation)
    {
        if (onStation.Count == 1)
        {
            return onStation[0];
        }

        static int Priority(SessionStatus status) => status switch
        {
            SessionStatus.Open => 0,
            SessionStatus.Paused => 1,
            SessionStatus.BillOpen => 2,
            SessionStatus.AwaitingPayment => 3,
            SessionStatus.ManualSettle => 4,
            _ => 5,
        };

        return onStation
            .OrderBy(s => Priority(s.Status))
            .ThenByDescending(s => s.StartedAtUtc)
            .First();
    }

    private static string MapStatus(PlaySessionEntity session) => session.Status switch
    {
        SessionStatus.Open => "Playing",
        SessionStatus.Paused => "Paused",
        SessionStatus.BillOpen or SessionStatus.AwaitingPayment => "Waiting for payment",
        SessionStatus.ManualSettle => "Manual settle",
        _ => "Free",
    };
}

public sealed class OperatorInvoiceService(
    AppDbContext db,
    AuditService audit,
    OperatorContext context,
    OperatorPolicies policies,
    OperatorMemberBillingService memberBilling)
{
    public async Task<InvoiceEntity> GetAsync(long number, CancellationToken ct)
    {
        var invoice = await db.Invoices.AsNoTracking()
            .Include(i => i.PaymentLines)
            .Include(i => i.Customer)
            .Include(i => i.Session)!.ThenInclude(s => s!.Station)
            .FirstOrDefaultAsync(i => i.Number == number, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "فاکتور یافت نشد.");
        return invoice;
    }

    public async Task SetOperatorNoteAsync(long number, string? note, CancellationToken ct)
    {
        if (context.Role == UserRole.Operator || context.Role is UserRole.Manager or UserRole.Owner)
        {
            var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Number == number, ct)
                ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "فاکتور یافت نشد.");
            invoice.OperatorNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
            await audit.AppendAsync("invoice.operator_note", new { number, note = invoice.OperatorNote }, ct);
            await db.SaveChangesAsync(ct);
            return;
        }

        throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "مجاز نیست.");
    }

    public async Task VoidAsync(long number, string? reason, CancellationToken ct)
    {
        if (context.Role is not UserRole.Manager and not UserRole.Owner)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "ابطال فاکتور فقط برای مدیر است.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "reason_required", "دلیل ابطال الزامی است.");
        }

        var invoice = await db.Invoices.FirstOrDefaultAsync(i => i.Number == number, ct)
            ?? throw new ApiException(StatusCodes.Status404NotFound, "not_found", "فاکتور یافت نشد.");
        if (invoice.Status == "void")
        {
            return;
        }

        var before = new { invoice.Status, invoice.CollectibleToman };
        invoice.Status = "void";
        invoice.VoidReason = reason.Trim();
        await memberBilling.ReverseInvoiceEffectsAsync(invoice, ct);
        await audit.AppendAsync("invoice.void", new { number, reason, before, after = new { invoice.Status } }, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<object>> ListPendingCardToCardAsync(CancellationToken ct)
    {
        var rows = await db.PaymentLines.AsNoTracking()
            .Include(p => p.Invoice)
            .Where(p => p.Method == PaymentMethod.CardToCard && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .OrderBy(p => p.Id)
            .Select(p => new PendingCardToCardRow(
                p.Id,
                p.Invoice!.Number,
                p.AmountToman,
                p.DestinationCardId,
                p.Reference,
                p.Invoice!.SessionId))
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    private sealed record PendingCardToCardRow(
        long Id,
        long InvoiceNumber,
        long AmountToman,
        string? DestinationCardId,
        string? Reference,
        Guid? SessionId);

    public async Task ConfirmCardToCardAsync(long paymentLineId, CancellationToken ct)
    {
        if (!policies.OperatorMayConfirmCardToCard)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "تأیید کارت‌به‌کارت غیرفعال است.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.PaymentLines
            .Where(p => p.Id == paymentLineId && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ConfirmationStatus, PaymentConfirmationStatus.Confirmed),
                ct);
        if (updated == 0)
        {
            var exists = await db.PaymentLines.AsNoTracking().AnyAsync(p => p.Id == paymentLineId, ct);
            if (!exists)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "not_found", "خط پرداخت یافت نشد.");
            }

            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "این پرداخت در انتظار نیست.");
        }

        await audit.AppendAsync("payment.card_to_card_confirmed", new { paymentLineId }, ct);
        await tx.CommitAsync(ct);
    }

    public async Task RejectCardToCardAsync(long paymentLineId, CancellationToken ct)
    {
        if (!policies.OperatorMayConfirmCardToCard)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "تأیید کارت‌به‌کارت غیرفعال است.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var updated = await db.PaymentLines
            .Where(p => p.Id == paymentLineId && p.ConfirmationStatus == PaymentConfirmationStatus.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.ConfirmationStatus, PaymentConfirmationStatus.Rejected),
                ct);
        if (updated == 0)
        {
            var exists = await db.PaymentLines.AsNoTracking().AnyAsync(p => p.Id == paymentLineId, ct);
            if (!exists)
            {
                throw new ApiException(StatusCodes.Status404NotFound, "not_found", "خط پرداخت یافت نشد.");
            }

            throw new ApiException(StatusCodes.Status409Conflict, "not_pending", "این پرداخت در انتظار نیست.");
        }

        var line = await db.PaymentLines.AsNoTracking().FirstAsync(p => p.Id == paymentLineId, ct);
        if (policies.RejectedCardToCardGoesToManagerUnpaidList)
        {
            db.RejectedCardToCard.Add(new RejectedCardToCardEntity
            {
                PaymentLineId = line.Id,
                AmountToman = line.AmountToman,
                RecordedAtUtc = DateTime.UtcNow,
            });
        }

        await audit.AppendAsync("payment.card_to_card_rejected", new { paymentLineId }, ct);
        await tx.CommitAsync(ct);
    }
}

public sealed class ManualSettleQueueService(AppDbContext db, OperatorPolicies policies, OperatorContext context)
{
    public async Task<IReadOnlyList<object>> ListAsync(CancellationToken ct)
    {
        var rows = await db.Sessions.AsNoTracking()
            .Include(s => s.Station)
            .Where(s => s.Status == SessionStatus.ManualSettle
                || (s.Status == SessionStatus.AwaitingPayment && s.ManualSettleReason != null))
            .OrderBy(s => s.StartedAtUtc)
            .Select(s => new
            {
                sessionId = s.Id,
                stationId = s.StationId,
                startedAtUtc = s.StartedAtUtc,
                lastCheckpointUtc = s.LastCheckpointUtc,
                reason = s.ManualSettleReason,
            })
            .ToListAsync(ct);
        return rows.Cast<object>().ToList();
    }

    public void EnsureMayResolve()
    {
        if (!policies.OperatorMayResolveManualSettle && context.Role == UserRole.Operator)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "forbidden", "تسویهٔ دستی فقط برای مدیر است.");
        }
    }
}
