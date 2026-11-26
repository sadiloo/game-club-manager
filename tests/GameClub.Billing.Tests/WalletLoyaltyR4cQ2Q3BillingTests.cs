using GameClub.Billing;

namespace GameClub.Billing.Tests;

/// <summary>Owner rulings R4c Q2/Q3 (2026-11-24): mixed wallet+cash uses one bill-level total; cash remainder is never rounded alone.</summary>
public sealed class WalletLoyaltyR4cQ2Q3BillingTests
{
    private static readonly RoundingPolicy Step5Up = RoundingPolicy.Create(5_000, RoundingDirection.Up);
    private static readonly DateTimeOffset Issued = T.At(2026, 6, 3, 15, 0);

    private static void AssertQ2Collect(InvoiceRequest request)
    {
        const long net = 3_000;
        const long wallet = 1_000;
        const long cash = 2_000;
        var tenders = new[]
        {
            new Tender(PaymentMethod.Wallet, wallet, "m"),
            new Tender(PaymentMethod.Cash, cash),
        };
        var (invoice, alloc) = InvoiceCalculator.Collect(request, tenders, [new MemberWallet("m", wallet, 0)]);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(net, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        Assert.Equal(net, invoice.PayableToman);
        Assert.Equal(wallet, alloc.Tenders.First(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(cash, alloc.Tenders.First(t => t.Method == PaymentMethod.Cash).AmountToman);
    }

    private static void AssertQ3Collect(InvoiceRequest request)
    {
        const long wallet = 50_001;
        const long cash = 99_999;
        const long collectible = 150_000;
        Assert.Equal(collectible, InvoiceCalculator.Calculate(request).PayableToman);
        var tenders = new[]
        {
            new Tender(PaymentMethod.Wallet, wallet, "m"),
            new Tender(PaymentMethod.Cash, cash),
        };
        var (invoice, alloc) = InvoiceCalculator.Collect(request, tenders, [new MemberWallet("m", wallet, 0)]);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(collectible, alloc.PayableToman);
        Assert.Equal(150, alloc.RoundingToman);
        Assert.Equal(collectible, invoice.PayableToman);
        Assert.Equal(wallet, alloc.Tenders.First(t => t.Method == PaymentMethod.Wallet).AmountToman);
        Assert.Equal(cash, alloc.Tenders.First(t => t.Method == PaymentMethod.Cash).AmountToman);
    }

    [Fact]
    public void r4c_q2_mixed_session_time_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 3_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_buffet_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 3_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_game_service_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [],
            [],
            [new ServiceLine("game", "بازی", 3_000)],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q2_mixed_time_buffet_3000_wallet_1000_cash_2000_exact_total()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 2_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("tea", "چای", 1, 1_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ2Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_session_time_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 149_850, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_buffet_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 149_850)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_game_service_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [],
            [],
            [new ServiceLine("game", "بازی", 149_850)],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_q3_mixed_time_buffet_149850_wallet_50001_cash_99999_whole_bill_once()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 140_000, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [new BuffetLine("tea", "چای", 1, 9_850)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        AssertQ3Collect(request);
    }

    [Fact]
    public void r4c_preview_buffet_12000_wallet_1000_only_collectible_15000()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 12_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 1_000, "m")],
            [new MemberWallet("m", 10_000, 0)]);
        Assert.False(alloc.IsValid);
        Assert.Equal(15_000, alloc.PayableToman);
        Assert.Equal(3_000, alloc.RoundingToman);
        Assert.Single(alloc.Errors, e => e.Code == PaymentErrors.SumMismatch);
        Assert.Equal(15_000, InvoiceCalculator.Calculate(request, [new Tender(PaymentMethod.Wallet, 1_000, "m")], [new MemberWallet("m", 10_000, 0)]).PayableToman);
    }

    [Fact]
    public void r4c_preview_time_149850_wallet_50001_only_collectible_150000()
    {
        var request = new InvoiceRequest(
            [new TimeChargeLine("time", 149_850, CafeFixture.Ps5, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 50_001, "m")],
            [new MemberWallet("m", 50_001, 0)]);
        Assert.False(alloc.IsValid);
        Assert.Equal(150_000, alloc.PayableToman);
        Assert.Equal(150, alloc.RoundingToman);
        Assert.Single(alloc.Errors, e => e.Code == PaymentErrors.SumMismatch);
    }

    [Fact]
    public void r4c_preview_buffet_3000_wallet_1000_only_collectible_3000()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 3_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 1_000, "m")],
            [new MemberWallet("m", 10_000, 0)]);
        Assert.False(alloc.IsValid);
        Assert.Equal(3_000, alloc.PayableToman);
        Assert.Equal(0, alloc.RoundingToman);
        Assert.Single(alloc.Errors, e => e.Code == PaymentErrors.SumMismatch);
    }

    [Fact]
    public void r4c_q2_underpay_3000_wallet_1000_cash_1999_single_sum_mismatch()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 3_000)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 1_000, "m"), new Tender(PaymentMethod.Cash, 1_999)],
            [new MemberWallet("m", 10_000, 0)]);
        Assert.False(alloc.IsValid);
        Assert.Equal(3_000, alloc.PayableToman);
        Assert.Single(alloc.Errors, e => e.Code == PaymentErrors.SumMismatch);
    }

    [Fact]
    public void r4c_q3_underpay_149850_wallet_50001_cash_99998_single_sum_mismatch()
    {
        var request = new InvoiceRequest(
            [],
            [new BuffetLine("custom", "خدمت", 1, 149_850)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);
        var (_, alloc) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.Wallet, 50_001, "m"), new Tender(PaymentMethod.Cash, 99_998)],
            [new MemberWallet("m", 50_001, 0)]);
        Assert.False(alloc.IsValid);
        Assert.Equal(150_000, alloc.PayableToman);
        Assert.Single(alloc.Errors, e => e.Code == PaymentErrors.SumMismatch);
    }

  private static InvoiceRequest BuffetRequest(long net)
        => new(
            [],
            [new BuffetLine("custom", "خدمت", 1, net)],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);

    private static InvoiceRequest PcTimeRequest(long net)
        => new(
            [new TimeChargeLine("time", net, PilotSeed.PcNormalGroupId, new LocalDate(2026, 6, 3), DayOfWeek.Tuesday, IsPcTime: true)],
            [],
            [],
            null,
            null,
            DiscountPolicy.Default,
            Step5Up,
            Issued);

    [Fact]
    public void q_b_ruled_apply_to_wallet_off_mixed_12000_whole_bill_15000()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(12_000),
            [new Tender(PaymentMethod.Wallet, 6_000, "m"), new Tender(PaymentMethod.Cash, 9_000)],
            [new MemberWallet("m", 20_000, 0)],
            roundingSettings: settings);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_000, alloc.PayableToman);
        Assert.NotEqual(16_000, alloc.PayableToman);
        Assert.Equal(3_000, alloc.RoundingToman);
        var (_, under) = InvoiceCalculator.Collect(
            BuffetRequest(12_000),
            [new Tender(PaymentMethod.Wallet, 6_000, "m"), new Tender(PaymentMethod.Cash, 6_000)],
            [new MemberWallet("m", 20_000, 0)],
            roundingSettings: settings);
        Assert.False(under.IsValid);
        Assert.Equal(15_000, under.PayableToman);
    }

    [Fact]
    public void gate_mutant_qb_cash_part_only_round_apply_wallet_off_mixed_not_16000()
    {
        q_b_ruled_apply_to_wallet_off_mixed_12000_whole_bill_15000();
    }

    [Fact]
    public void q_a_ruled_pc_balance_mixed_149850_collectible_150000()
    {
        var pcPocket = new WalletPocket(50_001, 0);
        var (_, alloc) = InvoiceCalculator.Collect(
            PcTimeRequest(149_850),
            [new Tender(PaymentMethod.PcBalance, 50_001), new Tender(PaymentMethod.Cash, 99_999)],
            pcBalance: pcPocket);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(150_000, alloc.PayableToman);
        Assert.NotEqual(151_000, alloc.PayableToman);
        Assert.Equal(150, alloc.RoundingToman);
    }

    [Fact]
    public void gate_mutant_qa_wallet_only_balance_applied_pc_balance_149850_fails_whole_bill()
    {
        q_a_ruled_pc_balance_mixed_149850_collectible_150000();
    }

    [Fact]
    public void q_a_ruled_pc_balance_12000_pc1000_cash14000_valid_cash15000_invalid()
    {
        var pcPocket = new WalletPocket(1_000, 0);
        var request = PcTimeRequest(12_000);
        var (_, ok) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.PcBalance, 1_000), new Tender(PaymentMethod.Cash, 14_000)],
            pcBalance: pcPocket);
        Assert.True(ok.IsValid, ok.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_000, ok.PayableToman);
        var (_, bad) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.PcBalance, 1_000), new Tender(PaymentMethod.Cash, 15_000)],
            pcBalance: pcPocket);
        Assert.False(bad.IsValid);
        Assert.Equal(15_000, bad.PayableToman);
        Assert.Contains(bad.Errors, e => e.Code == PaymentErrors.SumMismatch);
    }

    [Fact]
    public void f1_ruled_writeoff_wallet_rounds_like_cash_false_mixed_15500_unrounded()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var (_, ok) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Wallet, 500, "m"), new Tender(PaymentMethod.Cash, 15_000)],
            [new MemberWallet("m", 10_000, 0)],
            walletRoundsLikeCash: false,
            roundingSettings: settings);
        Assert.True(ok.IsValid, ok.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_500, ok.PayableToman);
        Assert.Equal(0, ok.RoundingToman);
        var (_, over) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Wallet, 500, "m"), new Tender(PaymentMethod.Cash, 19_500)],
            [new MemberWallet("m", 10_000, 0)],
            walletRoundsLikeCash: false,
            roundingSettings: settings);
        Assert.False(over.IsValid);
        Assert.Equal(15_500, over.PayableToman);
        Assert.NotEqual(20_000, over.PayableToman);
    }

    [Fact]
    public void gate_mutant_f1_writeoff_apply_wallet_off_whole_bill_round_reverts_to_20000()
    {
        f1_ruled_writeoff_wallet_rounds_like_cash_false_mixed_15500_unrounded();
    }

    [Fact]
    public void r14b_aon_writeoff_cash_15500_rejected_collectible_20000()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Cash, 15_500)],
            walletRoundsLikeCash: false);
        Assert.False(alloc.IsValid);
        Assert.Equal(20_000, alloc.PayableToman);
        Assert.Equal(4_500, alloc.RoundingToman);
    }

    [Fact]
    public void r14b_aon_writeoff_cash_20000_settles_payable_20000()
    {
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Cash, 20_000)],
            walletRoundsLikeCash: false);
        Assert.True(alloc.IsValid, alloc.Errors.FirstOrDefault()?.Message);
        Assert.Equal(20_000, alloc.PayableToman);
    }

    [Fact]
    public void gate_mutant_r14b_aon_writeoff_allocate_rounding_off_reverts_payable_15500()
    {
        r14b_aon_writeoff_cash_20000_settles_payable_20000();
    }

    [Fact]
    public void r14_f1_writeoff_apply_wallet_off_cash_only_20000_payable_15500()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var (_, ok) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Cash, 20_000)],
            walletRoundsLikeCash: false,
            roundingSettings: settings);
        Assert.True(ok.IsValid, ok.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_500, ok.PayableToman);
        Assert.Equal(0, ok.RoundingToman);
    }

    [Fact]
    public void r14_f1_writeoff_apply_wallet_off_wallet_only_500_payable_15500_preview_shape()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var (_, partial) = InvoiceCalculator.Collect(
            BuffetRequest(15_500),
            [new Tender(PaymentMethod.Wallet, 500, "m")],
            [new MemberWallet("m", 10_000, 0)],
            walletRoundsLikeCash: false,
            roundingSettings: settings);
        Assert.False(partial.IsValid);
        Assert.Equal(15_500, partial.PayableToman);
        Assert.Equal(0, partial.RoundingToman);
        Assert.NotEqual(20_000, partial.PayableToman);
    }

    [Fact]
    public void q_a_ruled_apply_wallet_off_pc_balance_12000_pc1000_cash14000_valid_15000()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var pcPocket = new WalletPocket(1_000, 0);
        var request = PcTimeRequest(12_000);
        var (_, ok) = InvoiceCalculator.Collect(
            request,
            [new Tender(PaymentMethod.PcBalance, 1_000), new Tender(PaymentMethod.Cash, 14_000)],
            pcBalance: pcPocket,
            roundingSettings: settings);
        Assert.True(ok.IsValid, ok.Errors.FirstOrDefault()?.Message);
        Assert.Equal(15_000, ok.PayableToman);
        Assert.NotEqual(16_000, ok.PayableToman);
    }

    [Fact]
    public void gate_mutant_qa_apply_wallet_off_pc_balance_whole_bill_not_cash_only_16000()
    {
        q_a_ruled_apply_wallet_off_pc_balance_12000_pc1000_cash14000_valid_15000();
    }

    [Fact]
    public void q_b_ruled_apply_to_wallet_off_preview_buffet_12000_wallet_only_collectible_15000()
    {
        var settings = PaymentRoundingSettings.Default with { ApplyToWallet = false };
        var (_, alloc) = InvoiceCalculator.Collect(
            BuffetRequest(12_000),
            [new Tender(PaymentMethod.Wallet, 1_000, "m")],
            [new MemberWallet("m", 20_000, 0)],
            roundingSettings: settings);
        Assert.False(alloc.IsValid);
        Assert.Equal(15_000, alloc.PayableToman);
    }
}