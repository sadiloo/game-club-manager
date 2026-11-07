using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;

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
    CustomerPricing? PricingUsed);

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
        IReadOnlyList<Tender>? previewTenders = null)
    {
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        var pricing = customerId is null || member is null ? null : MemberSettingsMapper.BuildPricing(member, settings, ladder);
        var discountPolicy = MemberSettingsMapper.LoadDiscountPolicy(settings);
        var accountPolicy = MemberSettingsMapper.LoadAccountPolicy(settings);

        Settlement? packageSettlement = null;
        IReadOnlyList<TimeChargeLine> timeLines;
        if (member is not null && packages.Count > 0 && charge.TotalBilledMinutes > 0)
        {
            var pcAccount = new PcAccount(member.CustomerId.ToString(), charge.TotalAmountToman, packages);
            packageSettlement = AccountSettlement.Settle(
                charge,
                pcAccount,
                accountPolicy,
                BillingPolicy.Default,
                graceMinuteEnabled: false);
            var moneyLines = packageSettlement.MoneyTimeLines();
            timeLines = moneyLines.Count > 0 ? moneyLines : TimeChargeLine.FromSession(charge);
        }
        else
        {
            timeLines = TimeChargeLinesFromCharge(charge);
        }
        var buffet = OpenSaleMapper.ToBuffetLines(saleLines);
        var services = OpenSaleMapper.ToServiceLines(saleLines).ToList();
        if (surchargeToman > 0)
        {
            services.Add(new ServiceLine("manual-surcharge", "سورشارژ", surchargeToman));
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

        return new MemberBillResult(
            invoice.NetBeforeRoundingToman,
            invoice.RoundingToman,
            invoice.PayableToman,
            surchargeToman,
            invoice.TimeGrossToman,
            invoice.BuffetGrossToman,
            invoice.ServicesGrossToman,
            invoice.DiscountToman,
            billedToUtc,
            invoice,
            allocation,
            packageSettlement,
            pricing);
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

        return new MemberBillResult(
            invoice.NetBeforeRoundingToman,
            invoice.RoundingToman,
            invoice.PayableToman,
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
