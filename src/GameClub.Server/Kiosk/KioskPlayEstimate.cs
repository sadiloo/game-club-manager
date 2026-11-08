using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Time;

namespace GameClub.Server.Kiosk;

internal static class KioskPlayEstimate
{
    internal static CustomerAccount BuildAccount(CustomerMemberEntity member, WalletSettings wallet, IReadOnlyList<TimePackage> packages)
    {
        var pocket = MemberWalletMapper.ToPocket(member);
        return new CustomerAccount(member.CustomerId.ToString(), wallet, pocket, new WalletPocket(0, 0), packages);
    }

    internal static (PcPlayResult Settled, RemainingTimeEstimate Estimate) AfterAcceptedPlay(
        PlaySessionEntity session,
        CustomerMemberEntity member,
        CustomerAccount account,
        SessionCharge charge,
        string groupId,
        BranchMemberSettingsEntity settings,
        RateCard card,
        DateTimeOffset at)
    {
        var ladder = MemberSettingsMapper.LoadLadder(settings);
        var pricing = account.Settings.ApplyDiscountsToPc
            ? MemberSettingsMapper.BuildPricing(member, settings, ladder)
            : null;
        var discounts = account.Settings.ApplyDiscountsToPc
            ? MemberSettingsMapper.LoadDiscountPolicy(settings)
            : null;
        var settled = CustomerPlay.Settle(
            charge,
            account,
            pricing: pricing,
            discounts: discounts);
        var postAccount = settled.Account;
        var estimate = CustomerPlay.Estimate(
            postAccount,
            groupId,
            session.Mode,
            session.PlayerCount,
            at,
            card,
            pricing: pricing,
            discounts: discounts);
        return (settled, estimate);
    }

    internal static DateTimeOffset? NextPaidBandStart(RateCard card, string groupId, PlayMode mode, int playerCount, DateTimeOffset at)
    {
        for (var i = 1; i <= 24 * 60; i++)
        {
            var probe = at.AddMinutes(i);
            var quote = RateResolver.Resolve(card, SpecialDayCalendar.Empty, probe, groupId, mode, playerCount);
            if (quote.HourlyRateToman > 0)
            {
                return probe;
            }
        }

        return null;
    }

    internal static string? FormatFreeBandText(RateCard card, CustomerAccount account, string groupId, PlaySessionEntity session, DateTimeOffset at)
    {
        DateTimeOffset? bandStart = NextPaidBandStart(card, groupId, session.Mode, session.PlayerCount, at);
        if (bandStart is null)
        {
            for (var i = 30; i <= 24 * 60; i += 30)
            {
                var probe = at.AddMinutes(i);
                var quote = RateResolver.Resolve(card, SpecialDayCalendar.Empty, probe, groupId, session.Mode, session.PlayerCount);
                if (quote.HourlyRateToman > 0)
                {
                    bandStart = probe;
                    break;
                }
            }
        }

        if (bandStart is DateTimeOffset start)
        {
            var post = CustomerPlay.Estimate(
                account,
                groupId,
                session.Mode,
                session.PlayerCount,
                start,
                card);
            var local = TehranTime.Ensure(start);
            return $"رایگان تا {local:HH:mm}، سپس {post.TotalMinutes} دقیقه";
        }

        var horizon = CustomerPlay.Estimate(account, groupId, session.Mode, session.PlayerCount, at, card);
        return horizon.TotalMinutes > 0 ? $"رایگان، سپس {horizon.TotalMinutes} دقیقه" : null;
    }
}
