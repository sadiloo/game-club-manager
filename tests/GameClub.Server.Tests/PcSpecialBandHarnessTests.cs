using GameClub.Billing;
using GameClub.Server.Services;
using GameClub.Server.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcSpecialBandHarnessTests(GameClubWebApplicationFactory factory)
{
    [Fact]
    public async Task production_default_pc_special_band_is_00_00_to_08_30_tehran()
    {
        await S3TestHelpers.ResetAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
        var card = (await rates.LoadAsync()).Card;
        var normal = card.Bands.Single(b => b.Id == PilotSeed.PcNormalBandId);
        Assert.Equal(GameTimerReference.SpecialStart, normal.Start);
        Assert.Equal(GameTimerReference.SpecialEnd, normal.End);
        var vip = card.Bands.Single(b => b.Id == PilotSeed.PcVipBandId);
        Assert.Equal(GameTimerReference.SpecialStart, vip.Start);
        Assert.Equal(GameTimerReference.SpecialEnd, vip.End);
    }

    [Fact]
    public async Task harness_pc_band_override_inert_when_enabled_flag_false()
    {
        var local = new GameClubWebApplicationFactory
        {
            HarnessPcSpecialBand = new PcSpecialBandHarnessOptions
            {
                Enabled = false,
                PcNormalStart = "12:00",
                PcNormalEnd = "13:00",
            },
        };
        await S3TestHelpers.ResetAsync(local);
        await using var scope = local.Services.CreateAsyncScope();
        var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
        var card = (await rates.LoadAsync()).Card;
        var normal = card.Bands.Single(b => b.Id == PilotSeed.PcNormalBandId);
        Assert.Equal(new TimeOfDay(0, 0), normal.Start);
        Assert.Equal(new TimeOfDay(8, 30), normal.End);
    }

    [Fact]
    public async Task harness_pc_band_override_applies_when_enabled_in_test_host()
    {
        var local = new GameClubWebApplicationFactory
        {
            HarnessPcSpecialBand = new PcSpecialBandHarnessOptions
            {
                Enabled = true,
                PcNormalStart = "12:05",
                PcNormalEnd = "12:35",
                PcVipStart = "12:05",
                PcVipEnd = "12:35",
            },
        };
        await S3TestHelpers.ResetAsync(local);
        await using var scope = local.Services.CreateAsyncScope();
        var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
        var card = (await rates.LoadAsync()).Card;
        var normal = card.Bands.Single(b => b.Id == PilotSeed.PcNormalBandId);
        Assert.Equal(new TimeOfDay(12, 5), normal.Start);
        Assert.Equal(new TimeOfDay(12, 35), normal.End);
    }
}