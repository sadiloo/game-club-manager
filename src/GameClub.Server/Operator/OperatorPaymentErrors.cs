using System.Globalization;
using GameClub.Billing;

namespace GameClub.Server.Operator;

internal static class OperatorPaymentErrors
{
    public static string Localize(PaymentError? error, long? payableToman = null)
    {
        if (error is null)
        {
            return "پرداخت نامعتبر است.";
        }

        return error.Code switch
        {
            PaymentErrors.SumMismatch => SumMismatchMessage(payableToman),
            PaymentErrors.NegativeAmount => "مبلغ پرداخت نمی‌تواند منفی باشد.",
            PaymentErrors.WalletMissingMember => "پرداخت کیف پول به عضو پیوسته نیاز دارد.",
            PaymentErrors.UnknownWallet => "کیف پول عضو یافت نشد.",
            PaymentErrors.InsufficientWallet => "موجودی کیف پول کافی نیست.",
            PaymentErrors.UnknownMethod => "روش پرداخت نامعتبر است.",
            PaymentErrors.PcBalanceNoPcLine => "موجودی پی‌سی برای این صورتحساب مجاز نیست.",
            _ => "پرداخت نامعتبر است.",
        };
    }

    private static string SumMismatchMessage(long? payableToman)
    {
        if (payableToman is not long payable || payable < 0)
        {
            return "جمع پرداختی با مبلغ قابل پرداخت برابر نیست.";
        }

        var latin = payable.ToString("N0", CultureInfo.InvariantCulture).Replace(",", "٬", StringComparison.Ordinal);
        var digits = Ss14BillDisplay.ToPersianDigits(latin);
        return $"مبلغ قابل پرداخت {digits} تومان است";
    }
}