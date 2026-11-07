using GameClub.Billing;
using GameClub.Server.Data.Entities;

namespace GameClub.Server.Operator;

public static class MemberWalletMapper
{
    public static WalletPocket ToPocket(CustomerMemberEntity row)
        => new(
            row.RealToman,
            row.GiftToman,
            row.BasisRealToman,
            row.BasisGiftToman,
            row.PositiveDrawnToman,
            row.GiftDrawnFromBasisToman);

    public static void ApplyPocket(CustomerMemberEntity row, WalletPocket pocket)
    {
        row.RealToman = pocket.RealToman;
        row.GiftToman = pocket.GiftToman;
        row.BasisRealToman = pocket.BasisRealToman;
        row.BasisGiftToman = pocket.BasisGiftToman;
        row.PositiveDrawnToman = pocket.PositiveDrawnToman;
        row.GiftDrawnFromBasisToman = pocket.GiftDrawnFromBasisToman;
    }

    public static MemberWallet ToMemberWallet(Guid customerId, CustomerMemberEntity row)
        => new(customerId.ToString(), ToPocket(row));

    /// <summary>Real top-up clears real-money debt first, then adds spendable credit.</summary>
    public static WalletPocket ApplyRealTopUp(WalletPocket pocket, long realToman)
    {
        if (realToman < 0)
        {
            throw new BillingException(BillingErrors.InvalidTopUp, "A top-up cannot be negative.");
        }

        if (realToman == 0)
        {
            return pocket;
        }

        var real = pocket.RealToman;
        if (real < 0)
        {
            var debt = -real;
            var clear = realToman < debt ? realToman : debt;
            real += clear;
            realToman -= clear;
        }

        if (realToman > 0)
        {
            pocket = new WalletPocket(real, pocket.GiftToman, pocket.BasisRealToman, pocket.BasisGiftToman, pocket.PositiveDrawnToman, pocket.GiftDrawnFromBasisToman);
            pocket = WalletPocketMath.Add(pocket, realToman, 0);
            if (pocket.RealToman > 0 || pocket.GiftToman > 0)
            {
                var posReal = pocket.RealToman > 0 ? pocket.RealToman : 0;
                var posGift = pocket.GiftToman > 0 ? pocket.GiftToman : 0;
                pocket = new WalletPocket(
                    pocket.RealToman,
                    pocket.GiftToman,
                    posReal,
                    posGift,
                    0,
                    0);
            }
        }
        else
        {
            pocket = new WalletPocket(
                real,
                pocket.GiftToman,
                pocket.BasisRealToman,
                pocket.BasisGiftToman,
                pocket.PositiveDrawnToman,
                pocket.GiftDrawnFromBasisToman);
        }

        return pocket;
    }

    public static WalletPocket ApplyGiftTopUp(WalletPocket pocket, long giftToman)
    {
        if (giftToman < 0)
        {
            throw new BillingException(BillingErrors.InvalidTopUp, "Gift credit cannot be negative.");
        }

        if (giftToman == 0)
        {
            return pocket;
        }

        pocket = WalletPocketMath.Add(pocket, 0, giftToman);
        var posReal = pocket.RealToman > 0 ? pocket.RealToman : 0;
        var posGift = pocket.GiftToman > 0 ? pocket.GiftToman : 0;
        if (posReal + posGift > 0 && pocket.BasisRealToman + pocket.BasisGiftToman == 0)
        {
            pocket = new WalletPocket(pocket.RealToman, pocket.GiftToman, posReal, posGift, pocket.PositiveDrawnToman, pocket.GiftDrawnFromBasisToman);
        }

        return pocket;
    }
}

internal static class WalletPocketMath
{
    public static WalletPocket Add(WalletPocket pocket, long realToman, long giftToman)
    {
        var real = MoneyMath.CheckedAdd(pocket.RealToman, realToman);
        var gift = MoneyMath.CheckedAdd(pocket.GiftToman, giftToman);
        return new WalletPocket(
            real,
            gift,
            pocket.BasisRealToman,
            pocket.BasisGiftToman,
            pocket.PositiveDrawnToman,
            pocket.GiftDrawnFromBasisToman);
    }
}
