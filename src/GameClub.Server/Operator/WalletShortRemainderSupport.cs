using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Operator;

public sealed record WalletShortRemainderSnapshot(
    bool Applies,
    long RemainderToman,
    long WalletAutoDrawToman,
    bool DebtRemainderAllowed);

public static class WalletShortRemainderSupport
{
    public static WalletShortRemainderSnapshot Compute(
        long collectibleToman,
        CustomerMemberEntity? member,
        BranchMemberSettingsEntity settings,
        BranchRoundingEntity rounding,
        StationKind? stationKind)
    {
        if (member is null || collectibleToman <= 0)
        {
            return new WalletShortRemainderSnapshot(false, 0, 0, false);
        }

        var pocket = MemberWalletMapper.ToPocket(member);
        var positive = Math.Max(0, pocket.TotalToman);
        if (positive >= collectibleToman)
        {
            return new WalletShortRemainderSnapshot(false, 0, 0, false);
        }

        var remainder = collectibleToman - positive;
        var debtAllowed = rounding.AllowWalletShortBalanceDebt && DebtRemainderAllowed(
            member,
            settings,
            stationKind,
            remainder);

        return new WalletShortRemainderSnapshot(true, remainder, positive, debtAllowed);
    }

    private static bool DebtRemainderAllowed(
        CustomerMemberEntity member,
        BranchMemberSettingsEntity settings,
        StationKind? stationKind,
        long remainderToman)
    {
        var debt = MemberSettingsMapper.LoadDebtPolicy(settings);
        if (debt == DebtPolicy.None)
        {
            return false;
        }

        var pocket = MemberWalletMapper.ToPocket(member);
        if (stationKind == StationKind.Pc && pocket.TotalToman < 0 && !settings.AllowPcWhileInDebt)
        {
            return false;
        }
        var room = pocket.SpendRoom(debt);
        if (room == long.MaxValue)
        {
            return true;
        }

        var positiveDraw = Math.Max(0, pocket.TotalToman);
        var debtRoom = room - positiveDraw;
        return debtRoom >= remainderToman;
    }

    public static void ValidateShortRemainderChoice(
        WalletShortRemainderSnapshot snapshot,
        string? shortRemainderMethod)
    {
        if (!snapshot.Applies || string.IsNullOrWhiteSpace(shortRemainderMethod))
        {
            return;
        }

        if (string.Equals(shortRemainderMethod, "Debt", StringComparison.OrdinalIgnoreCase)
            && !snapshot.DebtRemainderAllowed)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداختی با مبلغ قابل دریافت برابر نیست.");
        }
    }

    public static void ValidateWalletShortBalancePolicy(
        WalletShortRemainderSnapshot snapshot,
        bool allowWalletShortBalanceDebt,
        OperatorBillService.PaymentInput? walletPayment)
    {
        if (walletPayment is null)
        {
            return;
        }

        ValidateShortRemainderChoice(snapshot, walletPayment.ShortRemainderMethod);

        if (allowWalletShortBalanceDebt)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(walletPayment.ShortRemainderMethod)
            && string.Equals(walletPayment.ShortRemainderMethod, "Debt", StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداختی با مبلغ قابل دریافت برابر نیست.");
        }

        if (snapshot.Applies && walletPayment.AmountToman > snapshot.WalletAutoDrawToman)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداختی با مبلغ قابل دریافت برابر نیست.");
        }
    }

    public static PaymentRoundingSettings ApplyExplicitCashRemainderFlags(
        PaymentRoundingSettings settings,
        IReadOnlyList<OperatorBillService.PaymentInput>? payments)
    {
        var wallet = payments?.FirstOrDefault(p => p.Method.Equals("Wallet", StringComparison.OrdinalIgnoreCase));
        if (wallet is not null
            && string.Equals(wallet.ShortRemainderMethod, "Cash", StringComparison.OrdinalIgnoreCase))
        {
            return settings with { SuppressWalletDebtDraw = true };
        }

        return settings;
    }

    public static void ValidateExplicitCashShortRemainder(
        WalletShortRemainderSnapshot snapshot,
        IReadOnlyList<OperatorBillService.PaymentInput> payments)
    {
        if (!snapshot.Applies)
        {
            return;
        }

        var wallet = payments.FirstOrDefault(p => p.Method.Equals("Wallet", StringComparison.OrdinalIgnoreCase));
        if (wallet is null
            || !string.Equals(wallet.ShortRemainderMethod, "Cash", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cashTotal = payments
            .Where(p => p.Method.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            .Sum(p => p.AmountToman);
        if (cashTotal != snapshot.RemainderToman)
        {
            throw new ApiException(
                StatusCodes.Status400BadRequest,
                "payment_invalid",
                "جمع پرداختی با مبلغ قابل دریافت برابر نیست.");
        }
    }

    public static List<OperatorBillService.PaymentInput> NormalizeWalletShortPayments(
        IReadOnlyList<OperatorBillService.PaymentInput> payments,
        WalletShortRemainderSnapshot snapshot,
        long collectibleToman)
    {
        var list = new List<OperatorBillService.PaymentInput>();
        var walletHandled = false;
        foreach (var p in payments)
        {
            if (p.Method.Equals("Wallet", StringComparison.OrdinalIgnoreCase) && !walletHandled)
            {
                walletHandled = true;
                if (string.IsNullOrWhiteSpace(p.ShortRemainderMethod))
                {
                    list.Add(p);
                    continue;
                }

                if (!snapshot.Applies)
                {
                    list.Add(p);
                    continue;
                }

                if (string.Equals(p.ShortRemainderMethod, "Debt", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(p with { AmountToman = collectibleToman });
                }
                else
                {
                    list.Add(p with { AmountToman = snapshot.WalletAutoDrawToman });
                }
            }
            else
            {
                list.Add(p);
            }
        }

        return list;
    }
}