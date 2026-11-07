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
    long PackageSalesToman,
    MethodTotals Received);

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
        foreach (var row in topUps.Where(t => t.Kind == WalletLedgerKind.TopUpReal && t.Method is not null))
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

    public static IncomeBreakdown BuildInvoiceIncome(InvoiceEntity invoice, IReadOnlyList<WalletLedgerEntryEntity> walletTopUpsForShift)
    {
        if (invoice.Status == "void")
        {
            return new IncomeBreakdown(0, 0, 0, 0, 0, 0, 0, new MethodTotals(0, 0, 0));
        }

        var package = invoice.CatalogLines.Any(l => l.Kind == SaleLineKind.Service && invoice.TimeGrossToman == 0 && invoice.BuffetGrossToman == 0)
            && invoice.TimeGrossToman == 0
            ? invoice.CollectibleToman
            : invoice.CatalogLines.Where(l => l.Kind == SaleLineKind.Service).Sum(l => l.LineTotalToman);

        var isPackageOnly = invoice.SessionId is null && invoice.TimeGrossToman == 0 && invoice.BuffetGrossToman == 0 && package > 0;

        var received = SumReceivedMethods(invoice.PaymentLines);
        return new IncomeBreakdown(
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            invoice.RoundingToman,
            invoice.SurchargeToman,
            0,
            isPackageOnly ? package : 0,
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
