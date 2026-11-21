using GameClub.Billing;
using GameClub.Server.Data.Entities;
using GameClub.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class Ss14FixR1ConsolePrepaidRecalculatorTests(GameClubWebApplicationFactory factory)
{
    /// <summary>D4: amount-prepaid recalc must add the 60s grace when plan seconds are not above active elapsed (ConsolePrepaidRecalculator:83).</summary>
    [Fact]
    public async Task FixR1_F8_M7_D4_recalculator_grace_minute_totals_1200_active_seconds()
    {
        await S3TestHelpers.ResetAsync(factory);
        using var scope = factory.Services.CreateScope();
        var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
        var snapshot = await rates.LoadAsync();

        factory.Clock.SetTehran(2026, 8, 8, 14, 0);
        var startUtc = factory.Clock.UtcNow.UtcDateTime;
        factory.Clock.SetTehran(2026, 8, 8, 14, 19);
        var nowUtc = factory.Clock.UtcNow.UtcDateTime;
        const long activeThroughNow = 19 * 60L;

        var groupId = GameTimerReference.Ps5GroupId;
        var session = new PlaySessionEntity
        {
            Id = Guid.NewGuid(),
            StationId = "ps5-m7",
            PrepaidKind = PrepaidSessionKind.Amount,
            PrepaidAmountBudgetToman = 30_000,
            Segments =
            [
                new PlaySegmentEntity
                {
                    SegmentKey = "a",
                    StartUtc = startUtc,
                    EndUtc = nowUtc,
                    StationId = "ps5-m7",
                    GroupId = groupId,
                    Mode = PlayMode.Offline,
                    PlayerCount = 1,
                    Kind = StationKind.Console,
                },
                new PlaySegmentEntity
                {
                    SegmentKey = "b",
                    StartUtc = nowUtc,
                    StationId = "ps5-m7",
                    GroupId = groupId,
                    Mode = PlayMode.Offline,
                    PlayerCount = 1,
                    Kind = StationKind.Console,
                    GameTitleId = 1,
                    GameHourlyRateToman = 150_000,
                    GameNightHourlyRateToman = 180_000,
                    SplitReason = GameTariffService.SplitReasonGameChange,
                },
            ],
        };

        var open = session.Segments[1];
        SessionPrepaidRecalculator.RecalcAmountPrepaid(session, open, snapshot, nowUtc);
        Assert.Equal(activeThroughNow + 60, session.PrepaidBookedActiveSeconds);
    }
}