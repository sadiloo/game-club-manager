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
    public void g2_harness_band_stays_at_least_three_hours_from_declared_run_window()
    {
        var startUtc = DateTime.UtcNow;
        var endUtc = startUtc.AddMinutes(25);
        var band = PcSpecialBandHarnessSchedule.BandAwayFromUtcWindow(startUtc, endUtc, TimeSpan.FromHours(3));
        for (var probe = startUtc; probe <= endUtc; probe = probe.AddMinutes(10))
        {
            var local = TehranTime.FromUtc(probe);
            var minute = (local.Hour * 60) + local.Minute;
            var startMinute = (band.Start.Hour * 60) + band.Start.Minute;
            var endMinute = (band.End.Hour * 60) + band.End.Minute;
            var inside = startMinute <= endMinute
                ? minute >= startMinute && minute < endMinute
                : minute >= startMinute || minute < endMinute;
            Assert.False(inside, $"Harness special band must not overlap G2 run window at {local:HH:mm} Tehran.");
        }
    }

    [Fact]
    public void live_test_guard_and_test_host_parse_explicit_pc_band_args_identically()
    {
        const string hostArgs = "--pc-normal-start 20:13 --pc-normal-end 20:43 --pc-vip-start 20:13 --pc-vip-end 20:43";
        var (_, harnessFromParser) = PcSpecialBandHostArgs.ParseFromCommandLine(hostArgs);
        var harnessFromGuard = PcBandHarnessDiagnostics.OptionsFromHostArgs(hostArgs);
        Assert.NotNull(harnessFromParser);
        Assert.NotNull(harnessFromGuard);
        Assert.Equal(harnessFromParser!.PcNormalStart, harnessFromGuard!.PcNormalStart);
        Assert.Equal(harnessFromParser.PcNormalEnd, harnessFromGuard.PcNormalEnd);
        Assert.Equal("explicit", PcBandHarnessDiagnostics.BandModeLabelForHostArgs(hostArgs));
        PcBandHarnessDiagnostics.AssertLiveTestHarnessActive(harnessFromGuard);
    }

    [Fact]
    public void live_test_guard_rejects_empty_and_production_pc_band_args()
    {
        Assert.Null(PcBandHarnessDiagnostics.OptionsFromHostArgs(null));
        Assert.Throws<InvalidOperationException>(() =>
            PcBandHarnessDiagnostics.LogLiveTestServerStart("test", null));
        var productionArgs = "--pc-normal-start 00:00 --pc-normal-end 08:30 --pc-vip-start 00:00 --pc-vip-end 08:30";
        var harness = PcBandHarnessDiagnostics.OptionsFromHostArgs(productionArgs);
        Assert.NotNull(harness);
        Assert.Throws<InvalidOperationException>(() =>
            PcBandHarnessDiagnostics.AssertLiveTestHarnessActive(harness));
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