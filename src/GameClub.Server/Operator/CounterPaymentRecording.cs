using GameClub.Billing;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Operator;

/// <summary>
/// Records counter payment lines: non-cash (card) as typed, wallet as drawn (PO-3), cash as payable minus other lines (no zero lines).
/// </summary>
public static class CounterPaymentRecording
{
    /// <summary>
    /// Mixed wallet+cash counter over-tender: Collect allocation uses exact cash (= collectible − wallet),
    /// while typed payments stay for change and recorded lines.
    /// </summary>
    public static IReadOnlyList<OperatorBillService.PaymentInput> PaymentsForCollectAllocation(
        long collectibleToman,
        long unroundedPayableToman,
        IReadOnlyList<OperatorBillService.PaymentInput> payments)
    {
        if (collectibleToman <= 0 || payments.Count == 0)
        {
            return payments;
        }

        var hasWallet = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
        var hasCash = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Cash);
        if (!hasWallet || !hasCash)
        {
            return payments;
        }

        var tendered = payments.Sum(p => p.AmountToman);
        if (tendered <= collectibleToman)
        {
            return payments;
        }

        var walletTotal = payments
            .Where(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet)
            .Sum(p => p.AmountToman);

        var cashNeeded = collectibleToman - walletTotal;
        if (cashNeeded <= 0)
        {
            return payments
                .Where(p => OperatorPaymentMethods.Parse(p.Method) != PaymentMethod.Cash)
                .ToList();
        }

        var result = new List<OperatorBillService.PaymentInput>(payments.Count);
        var cashAssigned = false;
        foreach (var pay in payments)
        {
            if (OperatorPaymentMethods.Parse(pay.Method) == PaymentMethod.Cash)
            {
                if (!cashAssigned)
                {
                    result.Add(pay with { AmountToman = cashNeeded });
                    cashAssigned = true;
                }
                else
                {
                    result.Add(pay with { AmountToman = 0 });
                }
            }
            else
            {
                result.Add(pay);
            }
        }

        return result;
    }

    public static long ComputeChangeToman(long collectibleToman, IReadOnlyList<OperatorBillService.PaymentInput> payments)
    {
        if (collectibleToman <= 0)
        {
            return 0;
        }

        var tendered = payments.Sum(p => p.AmountToman);
        return tendered > collectibleToman ? tendered - collectibleToman : 0;
    }

    public static bool NonCashExceedsCollectible(
        long collectibleToman,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        AllocationResult? allocation)
    {
        if (collectibleToman <= 0 || payments.Count == 0)
        {
            return false;
        }

        var walletOnly = payments.All(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
        var hasWallet = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
        var hasCash = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Cash);
        var hasCard = payments.Any(p =>
        {
            var method = OperatorPaymentMethods.Parse(p.Method);
            return method is PaymentMethod.CardReader or PaymentMethod.CardToCard;
        });
        if (walletOnly && !hasCard)
        {
            return false;
        }

        var tendered = payments.Sum(p => p.AmountToman);
        if (hasWallet && hasCash && !walletOnly && tendered > collectibleToman)
        {
            return false;
        }

        return SumNonCashRecorded(payments, allocation) > collectibleToman;
    }

    public static void EnsureRecordable(
        long collectibleToman,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        AllocationResult? allocation)
    {
        if (collectibleToman <= 0 || payments.Count == 0)
        {
            return;
        }

        var nonCashRecorded = SumNonCashRecorded(payments, allocation);
        if (nonCashRecorded > collectibleToman)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.");
        }

        var tendered = payments.Sum(p => p.AmountToman);
        if (tendered < collectibleToman)
        {
            var walletOnly = payments.All(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
            var walletCoversCollectible = walletOnly && nonCashRecorded >= collectibleToman;
            if (!walletCoversCollectible)
            {
                throw new ApiException(
                    StatusCodes.Status400BadRequest,
                    "payment_invalid",
                    OperatorPaymentErrors.Localize(new PaymentError(PaymentErrors.SumMismatch, ""), collectibleToman));
            }
        }
    }

    public static IReadOnlyList<RecordedPaymentLine> BuildRecordedLines(
        long collectibleToman,
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        AllocationResult? allocation,
        Guid? customerId)
    {
        EnsureRecordable(collectibleToman, payments, allocation);
        var recordWalletAsTyped = RecordWalletLineAsTypedAmount(payments);

        long cardRecorded = 0;
        long walletRecorded = 0;
        var lines = new List<RecordedPaymentLine>();
        OperatorBillService.PaymentInput? firstCash = null;

        foreach (var pay in payments)
        {
            var method = OperatorPaymentMethods.Parse(pay.Method);
            if (method is PaymentMethod.CardReader or PaymentMethod.CardToCard)
            {
                cardRecorded = MoneyMath.CheckedAdd(cardRecorded, pay.AmountToman);
                lines.Add(new RecordedPaymentLine(pay, method, pay.AmountToman, null, 0, 0));
                continue;
            }

            if (method == PaymentMethod.Wallet)
            {
                var alloc = allocation?.Tenders.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted);
                var recorded = WalletRecordedToman(pay, alloc, recordWalletAsTyped);
                walletRecorded = MoneyMath.CheckedAdd(walletRecorded, recorded);
                var realDrawn = alloc?.RealDrawnToman ?? 0;
                var giftDrawn = alloc?.GiftDrawnToman ?? 0;
                if (recordWalletAsTyped)
                {
                    (realDrawn, giftDrawn) = CapWalletDrawToTyped(pay.AmountToman, realDrawn, giftDrawn);
                }

                lines.Add(new RecordedPaymentLine(
                    pay,
                    method,
                    recorded,
                    customerId,
                    realDrawn,
                    giftDrawn));
                continue;
            }

            if (method == PaymentMethod.Cash)
            {
                firstCash ??= pay;
            }
        }

        var cashRecorded = collectibleToman - cardRecorded - walletRecorded;
        if (cashRecorded < 0)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداخت غیرنقدی از مبلغ قابل پرداخت بیشتر است.");
        }

        if (cashRecorded > 0 && firstCash is not null)
        {
            lines.Add(new RecordedPaymentLine(firstCash, PaymentMethod.Cash, cashRecorded, null, 0, 0));
        }

        return lines;
    }

    private static long SumNonCashRecorded(
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        AllocationResult? allocation)
    {
        var recordWalletAsTyped = RecordWalletLineAsTypedAmount(payments);
        long total = 0;
        foreach (var pay in payments)
        {
            var method = OperatorPaymentMethods.Parse(pay.Method);
            if (method is PaymentMethod.CardReader or PaymentMethod.CardToCard)
            {
                total = MoneyMath.CheckedAdd(total, pay.AmountToman);
            }
            else if (method == PaymentMethod.Wallet)
            {
                var alloc = allocation?.Tenders.FirstOrDefault(t => t.Method == PaymentMethod.Wallet && t.Accepted);
                total = MoneyMath.CheckedAdd(total, WalletRecordedToman(pay, alloc, recordWalletAsTyped));
            }
        }

        return total;
    }

    /// <summary>Mixed wallet+cash: wallet payment line and debit match typed wallet (R14e-A); silent lift is wallet-only.</summary>
    private static bool RecordWalletLineAsTypedAmount(IReadOnlyList<OperatorBillService.PaymentInput> payments)
    {
        var hasWallet = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Wallet);
        var hasCash = payments.Any(p => OperatorPaymentMethods.Parse(p.Method) == PaymentMethod.Cash);
        return hasWallet && hasCash;
    }

    private static (long RealDrawn, long GiftDrawn) CapWalletDrawToTyped(long typedWalletToman, long realDrawn, long giftDrawn)
    {
        var drawn = MoneyMath.CheckedAdd(realDrawn, giftDrawn);
        if (drawn <= typedWalletToman)
        {
            return (realDrawn, giftDrawn);
        }

        if (realDrawn >= typedWalletToman)
        {
            return (typedWalletToman, 0);
        }

        return (realDrawn, typedWalletToman - realDrawn);
    }

    private static long WalletRecordedToman(
        OperatorBillService.PaymentInput pay,
        TenderResult? alloc,
        bool recordWalletAsTyped)
    {
        if (recordWalletAsTyped)
        {
            return pay.AmountToman;
        }

        if (alloc is null)
        {
            return pay.AmountToman;
        }

        var drawn = MoneyMath.CheckedAdd(alloc.RealDrawnToman, alloc.GiftDrawnToman);
        if (drawn > pay.AmountToman)
        {
            return drawn;
        }

        return alloc.AmountToman > pay.AmountToman ? alloc.AmountToman : pay.AmountToman;
    }

    public sealed record RecordedPaymentLine(
        OperatorBillService.PaymentInput Source,
        PaymentMethod Method,
        long AmountToman,
        Guid? WalletCustomerId,
        long WalletRealDrawnToman,
        long WalletGiftDrawnToman);
}