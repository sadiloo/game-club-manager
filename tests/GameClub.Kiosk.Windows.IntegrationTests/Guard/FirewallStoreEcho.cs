#if NET48
#nullable disable
using System;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class FirewallStoreEcho
{
    private static readonly string FirewallProfileEchoScript = """
        Get-NetFirewallProfile -PolicyStore $store | ForEach-Object {
          Write-Output "FW_STORE_ECHO store=$store profile=$($_.Name) DefaultOutboundAction=$($_.DefaultOutboundAction.ToString()) AllowLocalFirewallRules=$($_.AllowLocalFirewallRules.ToString())"
        }
        """;

    private static readonly string FirewallProfileEchoRsopScript = """
        Get-NetFirewallProfile -PolicyStore $store -ErrorAction SilentlyContinue | ForEach-Object {
          Write-Output "FW_STORE_ECHO_RSOP store=$store profile=$($_.Name) DefaultOutboundAction=$($_.DefaultOutboundAction.ToString()) AllowLocalFirewallRules=$($_.AllowLocalFirewallRules.ToString())"
        }
        """;

    internal static void EchoAllStores(string label)
    {
        foreach (var store in new[] { "ActiveStore", "PersistentStore" })
        {
            var script = $"$store = '{store}'; {FirewallProfileEchoScript}";
            var echo = WindowsIntegrationRunPowerShell.Run(script, tolerateNonZeroExit: true);
            Console.WriteLine($"{echo.TrimEnd()} label={label}");
        }

        foreach (var store in new[] { "RSOP", "GroupPolicy" })
        {
            var script = $"$store = '{store}'; {FirewallProfileEchoRsopScript}";
            try
            {
                var echo = WindowsIntegrationRunPowerShell.Run(script, tolerateNonZeroExit: true);
                Console.WriteLine($"{echo.TrimEnd()} label={label}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FW_STORE_ECHO_RSOP store={store} read_failed={ex.Message} label={label}");
            }
        }
    }

    internal static string ApplyWithStoreEcho(this IInternetFirewallApplier applier, InternetGuardConfig config, bool on)
    {
        var result = applier.Apply(config, on);
        EchoAllStores($"after_apply on={on} result={result}");
        return result;
    }
}
#endif