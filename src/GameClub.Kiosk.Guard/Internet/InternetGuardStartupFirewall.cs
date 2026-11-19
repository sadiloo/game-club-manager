using System;
using System.IO;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Reconcile persistent-store firewall with verified state at Guard/pipe start (PR40-R15 iii).</summary>
internal static class InternetGuardStartupFirewall
{
    public static void ReconcileOnPipeStart(InternetGuardConfig config, IInternetFirewallApplier firewall)
    {
        _ = InternetFirewallLocalhostStoreCleanup.CleanupLegacyLocalhostArtifacts();
        var persisted = new InternetGuardStateStore().Load();
        var readback = InternetFirewallOffApplyReadback.Capture(config);
        var model = readback.Model;
        var firewallLooksOff = model.ActiveStoreGuardRuleCount > 0 || model.AnyActiveProfileBlock;
        var statePresent = File.Exists(InternetGuardPaths.StatePath) && persisted is not null;

        if (firewallLooksOff && !statePresent)
        {
            var scriptResult = firewall.RunResetScript();
            string reconcileOutcome;
            if (!string.Equals(scriptResult, "firewall_script_ok", StringComparison.Ordinal))
            {
                reconcileOutcome = scriptResult;
            }
            else
            {
                var post = InternetFirewallOffApplyReadback.Capture(config);
                reconcileOutcome = post.Model.ClassifyOnReset() ?? "applied";
            }

            GuardWatchdogLog.Append($"reconcile_reset={reconcileOutcome}");
        }
    }
}