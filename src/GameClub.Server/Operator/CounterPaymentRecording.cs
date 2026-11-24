using GameClub.Billing;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Operator;

/// <summary>
/// Records counter payment lines: non-cash (card) as typed, wallet as drawn (PO-3), cash as payable minus other lines (no zero lines).
/// </summary>
public static class CounterPaymentRecording
{
    public static long ComputeChangeToman(long collectibleToman, IReadOnlyList<OperatorBillService.PaymentInput> payments)
    {
        if (collectibleToman <= 0)
        {
            return 0;
        }

        var tendered = payments.Sum(p => p.AmountToman);
        return tendered > collectibleToman ? tendered - collectibleToman : 0;
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
                var recorded = WalletRecordedToman(pay, alloc);
                walletRecorded = MoneyMath.CheckedAdd(walletRecorded, recorded);
                lines.Add(new RecordedPaymentLine(
                    pay,
                    method,
                    recorded,
                    customerId,
                    alloc?.RealDrawnToman ?? 0,
                    alloc?.GiftDrawnToman ?? 0));
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
                total = MoneyMath.CheckedAdd(total, WalletRecordedToman(pay, alloc));
            }
        }

        return total;
    }

    private static long WalletRecordedToman(OperatorBillService.PaymentInput pay, TenderResult? alloc)
    {
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