namespace GameClub.Server.Tests;

internal static class WalletTopUpTestJson
{
    public static object Cash(long amountToman, long giftToman = 0, Guid? commandId = null)
        => TopUp("Cash", amountToman, giftToman, commandId);

    public static object CardReader(long amountToman, long giftToman = 0, Guid? commandId = null)
        => TopUp("CardReader", amountToman, giftToman, commandId);

    public static object CardToCard(
        long amountToman,
        long giftToman = 0,
        string? destinationCardId = null,
        string? reference = null,
        Guid? commandId = null)
        => new
        {
            method = "CardToCard",
            amountToman,
            giftToman,
            destinationCardId,
            reference,
            commandId = commandId ?? Guid.NewGuid(),
        };

    public static object TopUp(string method, long amountToman, long giftToman = 0, Guid? commandId = null)
        => new
        {
            method,
            amountToman,
            giftToman,
            commandId = commandId ?? Guid.NewGuid(),
        };
}