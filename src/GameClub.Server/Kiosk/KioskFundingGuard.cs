using GameClub.Billing;
using GameClub.Server.Data;
using GameClub.Server.Data.Entities;
using GameClub.Server.Operator;
using GameClub.Server.Services;
using GameClub.Server.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace GameClub.Server.Kiosk;

public sealed class KioskFundingGuard(
    AppDbContext db,
    OperatorMemberContext members,
    RateConfigService rates,
    IClock clock)
{
    public async Task EnsureMemberMayPlayAsync(Guid customerId, string stationId, CancellationToken ct)
    {
        var settings = await members.LoadSettingsAsync(ct);
        var wallet = MemberSettingsMapper.LoadWalletSettings(settings);
        var (member, packages) = await members.LoadMemberStateAsync(customerId, ct);
        if (member is null)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "no_credit", "Insufficient credit or packages for PC play.");
        }

        var pocket = MemberWalletMapper.ToPocket(member);
        var account = new CustomerAccount(customerId.ToString(), wallet, pocket, new WalletPocket(0, 0), packages);
        var snapshot = await rates.LoadAsync(ct);
        var station = await db.Stations.Include(s => s.Group).FirstAsync(s => s.Id == StationIdNormalizer.Normalize(stationId), ct);
        var groupId = station.GroupId ?? PilotSeed.PcNormalGroupId;
        var at = clock.UtcNow;
        var estimate = CustomerPlay.Estimate(account, groupId, PlayMode.Offline, 1, at, snapshot.Card);
        if (estimate.BlockedByDebt)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "debt_blocked", "PC play is blocked by shared wallet debt.");
        }

        if (estimate.TotalMinutes <= 0)
        {
            if (estimate.RateIsFree)
            {
                var boundary = KioskPlayEstimate.NextPaidBandStart(snapshot.Card, groupId, PlayMode.Offline, 1, at);
                if (boundary is not null)
                {
                    var post = CustomerPlay.Estimate(account, groupId, PlayMode.Offline, 1, boundary.Value, snapshot.Card);
                    if (post.TotalMinutes > 0)
                    {
                        return;
                    }
                }
            }

            throw new ApiException(StatusCodes.Status403Forbidden, "no_credit", "Insufficient credit or packages for PC play.");
        }
    }
}
