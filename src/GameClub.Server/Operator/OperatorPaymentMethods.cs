using GameClub.Billing;
using GameClub.Server.Services;

namespace GameClub.Server.Operator;

internal static class OperatorPaymentMethods
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "Cash",
        "CardReader",
        "CardToCard",
    };

    public static PaymentMethod Parse(string method)
    {
        if (string.IsNullOrWhiteSpace(method) || !Allowed.Contains(method))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_method", $"Unknown payment method '{method}'.");
        }

        return method switch
        {
            "Cash" => PaymentMethod.Cash,
            "CardReader" => PaymentMethod.CardReader,
            "CardToCard" => PaymentMethod.CardToCard,
            _ => throw new ApiException(StatusCodes.Status400BadRequest, "unknown_method", $"Unknown payment method '{method}'."),
        };
    }
}
