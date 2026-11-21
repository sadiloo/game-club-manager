using GameClub.Billing;

namespace GameClub.Server.Services;

/// <summary>Persian user-facing text for <see cref="BillingException"/> codes. Never expose <see cref="BillingException.Message"/> to clients.</summary>
public static class BillingExceptionUserMessages
{
    private const string Fallback = "کار انجام نشد. دوباره تلاش کنید.";

    private static readonly IReadOnlyDictionary<string, string> ByCode = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [BillingErrors.InvalidTariff] = "تعرفه نامعتبر است.",
        [BillingErrors.TariffNotFound] = "تعرفه پیدا نشد.",
        [BillingErrors.InvalidMoney] = "مبلغ باید عدد صحیح مثبت باشد.",
        [BillingErrors.InvalidTime] = "زمان نامعتبر است.",
        [BillingErrors.InvalidDate] = "تاریخ درست نیست.",
        [BillingErrors.InvalidSession] = "جلسه نامعتبر است.",
        [BillingErrors.PcPause] = "توقف کامپیوتر نامعتبر است.",
        [BillingErrors.InvalidAccount] = "حساب نامعتبر است.",
        [BillingErrors.InvalidPayment] = "پرداخت نامعتبر است.",
        [BillingErrors.InvalidInvoice] = "صورتحساب نامعتبر است.",
        [BillingErrors.InvalidPolicy] = "تنظیم نامعتبر است.",
        [BillingErrors.InvalidLevel] = "جدول سطح‌ها نامعتبر است.",
        [BillingErrors.InvalidTopUp] = "شارژ نامعتبر است.",
        [BillingErrors.NotEligible] = "امکان‌پذیر نیست.",
        [BillingErrors.OutsideWindow] = "خارج از بازهٔ مجاز است.",
        ["invalid_policy"] = "تنظیم نامعتبر است.",
        ["invalid_money"] = "مبلغ باید عدد صحیح مثبت باشد.",
        ["invalid_invoice"] = "صورتحساب نامعتبر است.",
    };

    public static string ForCode(string? code) =>
        !string.IsNullOrEmpty(code) && ByCode.TryGetValue(code, out var msg) ? msg : Fallback;
}