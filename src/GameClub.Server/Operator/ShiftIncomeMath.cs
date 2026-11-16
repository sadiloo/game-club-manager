using GameClub.Billing;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Operator;

public sealed record MethodTotals(long Cash, long CardReader, long CardToCardConfirmed);

public sealed record IncomeBreakdown(
    long PlayToman,
    long BuffetToman,
    long ServicesToman,
    long RoundingToman,
    long SurchargeToman,
    long WalletTopUpToman,
    long GiftIssuedToman,
    MethodTotals Received);

public sealed record PostCloseCorrectionsTotals(long Cash, long Card, long C2c, long Gift);

public static class ShiftIncomeMath
{
    public static bool IsReceivedPaymentLine(PaymentLineEntity line)
        => line.Method != PaymentMethod.Wallet
           && line.Method != PaymentMethod.PcBalance
           && (line.Method != PaymentMethod.CardToCard
               || line.ConfirmationStatus == PaymentConfirmationStatus.Confirmed
               || line.ConfirmationStatus == PaymentConfirmationStatus.Settled);

    public static MethodTotals SumReceivedMethods(IEnumerable<PaymentLineEntity> lines)
    {
        long cash = 0;
        long card = 0;
        long c2c = 0;
        foreach (var line in lines.Where(IsReceivedPaymentLine))
        {
            switch (line.Method)
            {
                case PaymentMethod.Cash:
                    cash = MoneyMath.CheckedAdd(cash, line.AmountToman);
                    break;
                case PaymentMethod.CardReader:
                    card = MoneyMath.CheckedAdd(card, line.AmountToman);
                    break;
                case PaymentMethod.CardToCard:
                    c2c = MoneyMath.CheckedAdd(c2c, line.AmountToman);
                    break;
            }
        }

        return new MethodTotals(cash, card, c2c);
    }

    public static MethodTotals SumWalletTopUpReceived(IEnumerable<WalletLedgerEntryEntity> topUps)
    {
        long cash = 0;
        long card = 0;
        long c2c = 0;
        foreach (var row in topUps.Where(t => t.Kind is WalletLedgerKind.TopUpReal or WalletLedgerKind.Correction && t.Method is not null))
        {
            switch (row.Method)
            {
                case PaymentMethod.Cash:
                    cash = MoneyMath.CheckedAdd(cash, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardReader:
                    card = MoneyMath.CheckedAdd(card, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardToCard:
                    c2c = MoneyMath.CheckedAdd(c2c, row.RealDeltaToman);
                    break;
            }
        }

        return new MethodTotals(cash, card, c2c);
    }

    public static MethodTotals SumWalletTopUpExpected(IEnumerable<WalletLedgerEntryEntity> topUps)
    {
        return SumWalletTopUpReceived(topUps.Where(t => !t.IsPostCloseAdjustment));
    }

    public static PostCloseCorrectionsTotals SumPostCloseCorrections(IEnumerable<WalletLedgerEntryEntity> rows)
    {
        long cash = 0;
        long card = 0;
        long c2c = 0;
        long gift = 0;
        foreach (var row in rows.Where(r => r.Kind == WalletLedgerKind.Correction && r.IsPostCloseAdjustment))
        {
            gift = MoneyMath.CheckedAdd(gift, row.GiftDeltaToman);
            switch (row.Method)
            {
                case PaymentMethod.Cash:
                    cash = MoneyMath.CheckedAdd(cash, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardReader:
                    card = MoneyMath.CheckedAdd(card, row.RealDeltaToman);
                    break;
                case PaymentMethod.CardToCard:
                    c2c = MoneyMath.CheckedAdd(c2c, row.RealDeltaToman);
                    break;
            }
        }

        return new PostCloseCorrectionsTotals(cash, card, c2c, gift);
    }

    public static IncomeBreakdown BuildInvoiceIncome(InvoiceEntity invoice, IReadOnlyList<WalletLedgerEntryEntity> walletTopUpsForShift)
    {
        if (invoice.Status == "void")
        {
            return new IncomeBreakdown(0, 0, 0, 0, 0, 0, 0, new MethodTotals(0, 0, 0));
        }

        var received = SumReceivedMethods(invoice.PaymentLines);
        return new IncomeBreakdown(
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            invoice.RoundingToman,
            invoice.SurchargeToman,
            0,
            0,
            received);
    }

    public static long ReceivedMoneyTotal(MethodTotals invoiceMethods, MethodTotals walletMethods)
    {
        var cash = MoneyMath.CheckedAdd(invoiceMethods.Cash, walletMethods.Cash);
        var card = MoneyMath.CheckedAdd(invoiceMethods.CardReader, walletMethods.CardReader);
        var c2c = MoneyMath.CheckedAdd(invoiceMethods.CardToCardConfirmed, walletMethods.CardToCardConfirmed);
        return MoneyMath.CheckedAdd(MoneyMath.CheckedAdd(cash, card), c2c);
    }
}
