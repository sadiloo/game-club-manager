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
        StationKind stationKind,
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
        var isPc = stationKind == StationKind.Pc
            && charge.Lines.Count > 0
            && charge.TotalBilledMinutes > 0;
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
        var pcWriteOff = pcChargeInvoice is not null
            && pcChargeInvoice.Lines.Any(l => l.Kind == InvoiceLineKind.WriteOff);
        var graceWaivedZeroPayable = pcChargeInvoice is not null
            && pcChargeInvoice.PayableToman == 0
            && pcChargeInvoice.GraceWaivedToman > 0
            && !pcWriteOff;
        if (usingCollect && customerId is Guid cid && member is not null)
        {
            var wallets = new[] { MemberWalletMapper.ToMemberWallet(cid, member) };
            var debt = MemberSettingsMapper.LoadDebtPolicy(settings);
            var collectRequest = graceWaivedZeroPayable
                ? request with { TimeCharges = [] }
                : pcWriteOff && pcChargeInvoice is not null
                    ? request with { TimeCharges = CollectableTimeLines(timeLines, pcChargeInvoice) }
                    : request;
            var (collected, alloc) = InvoiceCalculator.Collect(collectRequest, previewTenders!, wallets, debt);
            invoice = pcWriteOff && pcChargeInvoice is not null
                ? ComposePcOvershootInvoice(pcChargeInvoice, collected)
                : collected;
            allocation = alloc;
        }
        else if (pcChargeInvoice is not null && saleLines.Count == 0 && surchargeToman == 0)
        {
            invoice = pcChargeInvoice;
        }
        else if (pcChargeInvoice is not null && pcWriteOff)
        {
            var buffetOnly = InvoiceCalculator.Calculate(request with { TimeCharges = [] });
            invoice = ComposePcOvershootInvoice(pcChargeInvoice, buffetOnly);
        }
        else if (pcChargeInvoice is not null && (saleLines.Count > 0 || surchargeToman > 0))
        {
            var addOn = InvoiceCalculator.Calculate(request with { TimeCharges = [] });
            invoice = MergeBuffetWithPcPlay(pcChargeInvoice, addOn);
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

    private static IReadOnlyList<TimeChargeLine> CollectableTimeLines(
        IReadOnlyList<TimeChargeLine> lines,
        Invoice pcChargeInvoice)
    {
        if (lines.Count == 0)
        {
            return lines;
        }

        var payable = pcChargeInvoice.PayableToman;
        if (lines.Count == 1)
        {
            return [lines[0] with { AmountToman = payable, IsPcTime = true }];
        }

        var gross = MoneyMath.CheckedSum(lines.Select(l => l.AmountToman));
        if (gross <= payable)
        {
            return lines.Select(l => l with { IsPcTime = true }).ToArray();
        }

        var scaled = new TimeChargeLine[lines.Count];
        var assigned = 0L;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == lines.Count - 1)
            {
                scaled[i] = lines[i] with { AmountToman = Math.Max(0, payable - assigned), IsPcTime = true };
                continue;
            }

            var share = gross == 0 ? 0 : MoneyMath.MulDiv(payable, lines[i].AmountToman, gross);
            scaled[i] = lines[i] with { AmountToman = share, IsPcTime = true };
            assigned += share;
        }

        return scaled;
    }

    private static Invoice ComposePcOvershootInvoice(Invoice play, Invoice addOn)
    {
        var lines = new List<InvoiceLine>();
        lines.AddRange(play.Lines);
        lines.AddRange(addOn.Lines.Where(l => l.Kind is InvoiceLineKind.Buffet or InvoiceLineKind.Service or InvoiceLineKind.Rounding));
        var discount = MoneyMath.CheckedAdd(play.DiscountToman, addOn.DiscountToman);
        var net = MoneyMath.CheckedAdd(play.NetBeforeRoundingToman, addOn.BuffetGrossToman + addOn.ServicesGrossToman - addOn.DiscountToman);
        var rounding = MoneyMath.CheckedAdd(play.RoundingToman, addOn.RoundingToman);
        var payable = MoneyMath.CheckedAdd(play.PayableToman, addOn.BuffetGrossToman + addOn.ServicesGrossToman - addOn.DiscountToman);
        payable = MoneyMath.CheckedAdd(payable, addOn.RoundingToman);
        return new Invoice(
            lines,
            play.TimeGrossToman,
            addOn.BuffetGrossToman,
            addOn.ServicesGrossToman,
            discount,
            net,
            rounding,
            payable,
            addOn.RejectedCode,
            play.GraceWaivedToman);
    }

    private static Invoice MergeBuffetWithPcPlay(Invoice play, Invoice addOn)
    {
        var lines = new List<InvoiceLine>();
        lines.AddRange(play.Lines);
        lines.AddRange(addOn.Lines);
        var net = MoneyMath.CheckedAdd(play.NetBeforeRoundingToman, addOn.BuffetGrossToman + addOn.ServicesGrossToman);
        var payable = MoneyMath.CheckedAdd(play.PayableToman, addOn.PayableToman);
        return new Invoice(
            lines,
            play.TimeGrossToman,
            addOn.BuffetGrossToman,
            addOn.ServicesGrossToman,
            MoneyMath.CheckedAdd(play.DiscountToman, addOn.DiscountToman),
            net,
            MoneyMath.CheckedAdd(play.RoundingToman, addOn.RoundingToman),
            payable,
            addOn.RejectedCode,
            play.GraceWaivedToman);
    }
}
