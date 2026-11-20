using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Operator;

internal static class OperatorMoneyValidation
{
    /// <summary>Maximum inclusive money amount (15 decimal digits).</summary>
    public const long MaxToman = 999_999_999_999_999L;

    public static void EnsureNonNegativeBounded(long value, string code = "invalid_amount")
    {
        if (value < 0 || value > MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "مبلغ باید عدد صحیح مثبت باشد.");
        }
    }

    public static void EnsurePositiveBounded(long value, string code = "invalid_amount")
    {
        if (value <= 0 || value > MaxToman)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, code, "مبلغ باید عدد صحیح مثبت باشد.");
        }
    }
}