using GameClub.Billing;

namespace GameClub.Billing.Tests;

public sealed class PcInternetBillingTests
{
    private static RateCard SamplePcCard()
    {
        const long offDay = 60_000;
        const long offNight = 80_000;
        return new RateCardBuilder()
            .AddBand(PilotSeed.PcNormalSpecialHours)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalHourly)
            .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Online, null, PilotSeed.PcNormalSpecialHourly)
            .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, offDay)
            .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Offline, 1, offNight)
            .Build();
    }

    [Fact]
    public void PcInternet_E1_three_segments_whole_minutes_total_110000()
    {
        var start = T.At(2026, 6, 2, 17, 0);
        var card = SamplePcCard();
        var charge = SessionPricer.Price(new Session(
            "e1",
            [
                CafeFixture.Play("a", start, start.AddMinutes(45), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
                CafeFixture.Play("b", start.AddMinutes(45), start.AddMinutes(75), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Offline),
                CafeFixture.Play("c", start.AddMinutes(75), start.AddMinutes(90), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online),
            ]), card);
        Assert.Equal(110_000, charge.TotalAmountToman);
        Assert.Equal(90, charge.TotalBilledMinutes);
        Assert.Equal(3, charge.Lines.Count);
    }

    [Fact]
    public void PcInternet_E5_migration_parity_80000_and_130000()
    {
        var card = PilotSeed.CreateCard();
        var day = T.At(2026, 6, 2, 10, 0);
        var normal = SessionPricer.Price(
            CafeFixture.Session("e5n", CafeFixture.Play("a", day, day.AddMinutes(60), "pc-1", PilotSeed.PcNormalGroupId, 1, PlayMode.Online)),
            card);
        Assert.Equal(80_000, normal.TotalAmountToman);

        var nightStart = T.At(2026, 6, 2, 2, 0);
        var vip = SessionPricer.Price(
            CafeFixture.Session("e5v", CafeFixture.Play("a", nightStart, nightStart.AddMinutes(60), "pc-vip-1", PilotSeed.PcVipGroupId, 1, PlayMode.Online)),
            card);
        Assert.Equal(130_000, vip.TotalAmountToman);
    }
}