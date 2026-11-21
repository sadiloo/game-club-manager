using GameClub.Billing;

namespace GameClub.Server.Operator;

internal static class OperatorPaymentErrors
{
    public static string Localize(PaymentError? error)
    {
        if (error is null)
        {
            return "پرداخت نامعتبر است.";
        }

        return error.Code switch
        {
            PaymentErrors.SumMismatch => "جمع پرداختی با مبلغ قابل پرداخت برابر نیست.",
            PaymentErrors.NegativeAmount => "مبلغ پرداخت نمی‌تواند منفی باشد.",
            PaymentErrors.WalletMissingMember => "پرداخت کیف پول به عضو پیوسته نیاز دارد.",
            PaymentErrors.UnknownWallet => "کیف پول عضو یافت نشد.",
            PaymentErrors.InsufficientWallet => "موجودی کیف پول کافی نیست.",
            PaymentErrors.UnknownMethod => "روش پرداخت نامعتبر است.",
            PaymentErrors.PcBalanceNoPcLine => "موجودی پی‌سی برای این صورتحساب مجاز نیست.",
            _ => "پرداخت نامعتبر است.",
        };
    }
}