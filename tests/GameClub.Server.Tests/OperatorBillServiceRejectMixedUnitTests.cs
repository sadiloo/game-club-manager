using GameClub.Server.Operator;
using GameClub.Server.Services;
using Microsoft.AspNetCore.Http;

namespace GameClub.Server.Tests;

public sealed class OperatorBillServiceRejectMixedUnitTests
{
    private static BillAmounts Amounts(long unrounded, long collectible)
        => new(
            UnroundedPayableToman: unrounded,
            RoundingToman: collectible - unrounded,
            CollectibleToman: collectible,
            SurchargeToman: 0,
            TimeGrossToman: 0,
            BuffetGrossToman: unrounded,
            ServicesGrossToman: 0,
            BilledToUtc: DateTime.UtcNow);

    private static OperatorBillService.PaymentInput W(long v) => new("Wallet", v);
    private static OperatorBillService.PaymentInput C(long v) => new("Cash", v);

    [Fact]
    public void reject_mixed_wallet_cash_between_unrounded_and_collectible_400()
    {
        var amounts = Amounts(unrounded: 49_500, collectible: 50_000);
        var payments = new[] { W(49_000), C(500) };
        var ex = Assert.Throws<ApiException>(() => OperatorBillService.RejectMixedUnderRoundedCollectible(amounts, payments));
        Assert.Equal(StatusCodes.Status400BadRequest, ex.Status);
        Assert.Equal("payment_invalid", ex.Code);
    }

    [Fact]
    public void mutant_gate_reject_mixed_under_rounded_no_op_would_accept_gap()
    {
        var amounts = Amounts(unrounded: 49_500, collectible: 50_000);
        var payments = new[] { W(49_000), C(500) };
        try
        {
            OperatorBillService.RejectMixedUnderRoundedCollectible(amounts, payments);
            Assert.Fail("RejectMixedUnderRoundedCollectible must throw for mixed under-rounded collectible");
        }
        catch (ApiException ex)
        {
            Assert.Equal("payment_invalid", ex.Code);
        }
    }

    [Fact]
    public void shortfall_exemption_wallet_cash_at_or_above_collectible_does_not_throw()
    {
        var amounts = Amounts(unrounded: 49_500, collectible: 50_000);
        var payments = new[] { W(49_500), C(1_000) };
        OperatorBillService.RejectMixedUnderRoundedCollectible(amounts, payments);
    }

    [Fact]
    public void shortfall_exemption_below_unrounded_unchanged_wallet_only_path_unchecked()
    {
        var amounts = Amounts(unrounded: 49_500, collectible: 50_000);
        var payments = new[] { W(49_000) };
        OperatorBillService.RejectMixedUnderRoundedCollectible(amounts, payments);
    }
}