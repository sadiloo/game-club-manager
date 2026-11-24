using GameClub.Billing;
using GameClub.Server.Services;
using GameClub.Server.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GameClub.Server.Tests;

[Collection("server")]
public sealed class PcSpecialBandProductionIgnoresHarnessEnvTests
{
    [Fact]
    public async Task production_server_ignores_test_harness_pc_band_environment_variables()
    {
        var keys = new[]
        {
            "TestHarness__PcSpecialBand__Enabled",
            "TestHarness__PcSpecialBand__PcNormalStart",
            "TestHarness__PcSpecialBand__PcNormalEnd",
            "TestHarness__PcSpecialBand__PcVipStart",
            "TestHarness__PcSpecialBand__PcVipEnd",
        };
        var prior = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (var key in keys)
            {
                prior[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, key.EndsWith("Enabled", StringComparison.Ordinal) ? "true" : "15:00");
            }

            var factory = new GameClubWebApplicationFactory();
            await S3TestHelpers.ResetAsync(factory);
            await using var scope = factory.Services.CreateAsyncScope();
            var rates = scope.ServiceProvider.GetRequiredService<RateConfigService>();
            var card = (await rates.LoadAsync()).Card;
            var normal = card.Bands.Single(b => b.Id == PilotSeed.PcNormalBandId);
            Assert.Equal(GameTimerReference.SpecialStart, normal.Start);
            Assert.Equal(GameTimerReference.SpecialEnd, normal.End);
        }
        finally
        {
            foreach (var (key, value) in prior)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}