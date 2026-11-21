using GameClub.Billing;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Operator;

internal static class OperatorMoneyValidation
{
    /// <summary>Maximum inclusive money amount (15 decimal digits).</summary>
    public const long MaxToman = 999_999_999_999_999L;

    public static void EnsureNonNegativeBounded(long value, string code = "invalid_amount")
    {
        if (value < 0)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "مبلغ نمی‌تواند منفی باشد.");
        }

        if (value > MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "مبلغ از حد مجاز بیشتر است.");
        }
    }

    public static void EnsurePositiveBounded(long value, string code = "invalid_amount")
    {
        if (value <= 0 || value > MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "مبلغ باید عدد صحیح مثبت باشد.");
        }
    }

    public static void EnsureWalletBalances(long realToman, long giftToman, string amountCode = "invalid_amount")
    {
        if (realToman < -MaxToman || realToman > MaxToman)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                amountCode,
                realToman < 0 ? "بدهی از حد مجاز بیشتر است." : "مبلغ از حد مجاز بیشتر است.");
        }

        EnsureNonNegativeBounded(giftToman, "invalid_gift");
        var total = MoneyMath.CheckedAdd(realToman, giftToman);
        if (total > MaxToman)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                amountCode,
                "موجودی کیف پول از حد مجاز بیشتر می‌شود.");
        }
    }
}