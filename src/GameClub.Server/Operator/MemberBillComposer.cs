using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using GameClub.Server.Time;

namespace GameClub.Server.Operator;

public sealed record MemberBillResult(
    long UnroundedPayableToman,
    long RoundingToman,
    long CollectibleToman,
    long SurchargeToman,
    long TimeGrossToman,
    long BuffetGrossToman,
    long ServicesGrossToman,
    long DiscountToman,
    DateTime BilledToUtc,
    Invoice InvoicePreview,
    AllocationResult? Allocation,
    Settlement? PackageSettlement,
    CustomerPricing? PricingUsed,
    OperatorAlert? OvershootAlert = null);

public sealed class MemberBillComposer
{
    public static MemberBillResult Compute(
        SessionCharge charge,
        BranchRatesSnapshot rates,
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        long surchargeToman,
        DateTime billedToUtc,
        Guid? customerId,
        CustomerMemberEntity? member,
        BranchMemberSettingsEntity settings,
        IReadOnlyList<TimePackage> packages,
        IReadOnlyList<Tender>? previewTenders = null,
        bool applyMemberPricing = true)
    {
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        var pricing = customerId is null || member is null || !applyMemberPricing
            ? null
            : MemberSettingsMapper.BuildPricing(member, settings, ladder);
        var discountPolicy = MemberSettingsMapper.LoadDiscountPolicy(settings);
        var accountPolicy = MemberSettingsMapper.LoadAccountPolicy(settings);

        Settlement? packageSettlement = null;
        OperatorAlert? overshootAlert = null;
        Invoice? pcChargeInvoice = null;
        IReadOnlyList<TimeChargeLine> timeLines;
        var isPc = charge.Lines.Count > 0 && charge.Lines.All(l =>
            l.GroupId == PilotSeed.PcNormalGroupId
            || l.GroupId == PilotSeed.PcVipGroupId
            || l.GroupId.StartsWith("pc", StringComparison.Ordinal)
            || l.GroupId.Contains("custom", StringComparison.OrdinalIgnoreCase));
        if (member is not null && isPc && charge.TotalBilledMinutes > 0)
        {
            var wallet = MemberSettingsMapper.LoadWalletSettings(settings);
            var pocket = MemberWalletMapper.ToPocket(member);
            var account = new CustomerAccount(member.CustomerId.ToString(), wallet, pocket, new WalletPocket(0, 0), packages);
            var play = CustomerPlay.Settle(
                charge,
                account,
                accountPolicy,
                BillingPolicy.Default,
                pricing,
                discounts: discountPolicy,
                issuedAt: TimeMapping.ToTehran(billedToUtc));
            packageSettlement = play.Settlement;
            overshootAlert = play.Settlement.Alert;
            pcChargeInvoice = play.ChargeInvoice;
            timeLines = play.Settlement.MoneyTimeLines();
        }
        else if (member is not null && packages.Count > 0 && charge.TotalBilledMinutes > 0)
        {
            var pcAccount = new PcAccount(member.CustomerId.ToString(), charge.TotalAmountToman, packages);
            packageSettlement = AccountSettlement.Settle(
                charge,
                pcAccount,
                accountPolicy,
                BillingPolicy.Default,
                graceMinuteEnabled: false);
            timeLines = packageSettlement.MoneyTimeLines();
        }
        else
        {
            timeLines = TimeChargeLinesFromCharge(charge);
        }
        var buffet = OpenSaleMapper.ToBuffetLines(saleLines);
        var services = OpenSaleMapper.ToServiceLines(saleLines).ToList();
        var usingCollect = previewTenders is { Count: > 0 } && customerId is not null && member is not null;
        var surchargeInCollect = usingCollect && surchargeToman > 0;
        if (surchargeInCollect)
        {
            services.Add(new ServiceLine("pa10", "۵٬۰۰۰+", surchargeToman));
        }

        var issuedAt = TimeMapping.ToTehran(billedToUtc);
        var request = new InvoiceRequest(
            timeLines,
            buffet,
            services,
            pricing,
            null,
            discountPolicy,
            rates.Rounding,
            issuedAt);

        AllocationResult? allocation = null;
        Invoice invoice;
        if (usingCollect && customerId is Guid cid && member is not null)
        {
            var wallets = new[] { MemberWalletMapper.ToMemberWallet(cid, member) };
            var debt = MemberSettingsMapper.LoadDebtPolicy(settings);
            var (collected, alloc) = InvoiceCalculator.Collect(request, previewTenders!, wallets, debt);
            invoice = collected;
            allocation = alloc;
        }
        else if (pcChargeInvoice is not null && saleLines.Count == 0 && surchargeToman == 0)
        {
            invoice = pcChargeInvoice;
        }
        else
        {
            invoice = InvoiceCalculator.Calculate(request);
        }

        var postSurcharge = surchargeInCollect ? 0 : surchargeToman;
        var (unrounded, rounding, collectible) = ApplyPostRoundSurcharge(invoice, postSurcharge, usingCollect, allocation);
        var servicesGross = invoice.ServicesGrossToman;
        if (surchargeInCollect)
        {
            servicesGross = MoneyMath.CheckedAdd(servicesGross, -surchargeToman);
        }

        return new MemberBillResult(
            unrounded,
            rounding,
            collectible,
            surchargeToman,
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            servicesGross,
            invoice.DiscountToman,
            billedToUtc,
            invoice,
            allocation,
            packageSettlement,
            pricing,
            overshootAlert);
    }

    public static MemberBillResult ComputeBuffetOnly(
        IReadOnlyList<OpenSaleLineEntity> saleLines,
        BranchRatesSnapshot rates,
        Guid? customerId,
        CustomerMemberEntity? member,
        BranchMemberSettingsEntity settings,
        IReadOnlyList<Tender>? previewTenders = null)
    {
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        var pricing = customerId is null || member is null ? null : MemberSettingsMapper.BuildPricing(member, settings, ladder);
        var discountPolicy = MemberSettingsMapper.LoadDiscountPolicy(settings);
        var buffet = OpenSaleMapper.ToBuffetLines(saleLines);
        var services = OpenSaleMapper.ToServiceLines(saleLines);
        var issuedAt = TehranTime.Ensure(DateTimeOffset.UtcNow);
        var request = new InvoiceRequest([], buffet, services, pricing, null, discountPolicy, rates.Rounding, issuedAt);

        AllocationResult? allocation = null;
        Invoice invoice;
        if (previewTenders is { Count: > 0 } tenders && customerId is Guid cid && member is not null)
        {
            var wallets = new[] { MemberWalletMapper.ToMemberWallet(cid, member) };
            var debt = MemberSettingsMapper.LoadDebtPolicy(settings);
            var (collected, alloc) = InvoiceCalculator.Collect(request, tenders, wallets, debt);
            invoice = collected;
            allocation = alloc;
        }
        else
        {
            invoice = InvoiceCalculator.Calculate(request);
        }

        var (unrounded, rounding, collectible) = ApplyPostRoundSurcharge(invoice, 0, previewTenders is { Count: > 0 }, allocation);

        return new MemberBillResult(
            unrounded,
            rounding,
            collectible,
            0,
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            invoice.DiscountToman,
            issuedAt.UtcDateTime,
            invoice,
            allocation,
            null,
            pricing);
    }

    private static (long Unrounded, long Rounding, long Collectible) ApplyPostRoundSurcharge(
        Invoice invoice,
        long surchargeToman,
        bool usedCollect,
        AllocationResult? allocation)
    {
        var unrounded = MoneyMath.CheckedAdd(invoice.NetBeforeRoundingToman, surchargeToman);
        long collectible;
        long rounding;
        if (usedCollect && allocation is { IsValid: true })
        {
            collectible = MoneyMath.CheckedAdd(allocation.PayableToman, surchargeToman);
            rounding = collectible - unrounded;
        }
        else
        {
            collectible = MoneyMath.CheckedAdd(invoice.PayableToman, surchargeToman);
            rounding = collectible - unrounded;
        }

        return (unrounded, rounding, collectible);
    }

    private static IReadOnlyList<TimeChargeLine> TimeChargeLinesFromCharge(SessionCharge charge)
    {
        var lines = TimeChargeLine.FromSession(charge);
        if (lines.Count == 0 || charge.Lines.Count == 0)
        {
            return lines;
        }

        var isPc = charge.Lines[0].GroupId is string g
            && (g == PilotSeed.PcNormalGroupId || g == PilotSeed.PcVipGroupId);
        if (!isPc)
        {
            return lines;
        }

        return lines.Select(l => l with { IsPcTime = true }).ToArray();
    }
}
