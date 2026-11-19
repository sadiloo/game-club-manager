#if NET48
using System;
using GameClub.Kiosk.Guard.Internet;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

[Trait("Category", "PcInternetFirewall")]
public sealed class WindowsPcInternetProductionGuardTests
{
    [Fact]
    public void production_guard_applier_ignores_checkpoint_internet_apply_fault_environment_variable()
    {
        var prior = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT");
        try
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT", "1");
            var applier = new InternetFirewallApplier();
            var config = new InternetGuardConfig
            {
                ServerIps = Array.Empty<string>(),
                ServerPort = 443,
            };
            var result = applier.Apply(config, on: false);
            Assert.NotEqual("apply_failed:harness_injected", result);
            Assert.Equal("firewall_not_configured", result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_FAULT", prior);
        }
    }
}
#endif