using GameClub.Billing;
using GameClub.Server.Services;

namespace GameClub.Server.Operator;

internal static class OperatorPaymentMethods
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cash",
        "CardReader",
        "CardToCard",
        "Wallet",
    };

    public static PaymentMethod Parse(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || !Allowed.Contains(method))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_method", $"Unknown payment method '{method}'.");
        }

        if (method.Equals("Cash", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentMethod.Cash;
        }

        if (method.Equals("CardReader", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentMethod.CardReader;
        }

        if (method.Equals("Wallet", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentMethod.Wallet;
        }

        return PaymentMethod.CardToCard;
    }
}
