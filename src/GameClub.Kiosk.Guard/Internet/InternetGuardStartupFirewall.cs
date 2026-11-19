using System;
using System.Diagnostics;
using System.IO;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Reconcile persistent-store firewall with verified state at Guard/pipe start (PR40-R15 iii).</summary>
internal static class InternetGuardStartupFirewall
{
    public static void ReconcileOnPipeStart(InternetGuardConfig config, IInternetFirewallApplier firewall)
    {
        var total = Stopwatch.StartNew();
        var step = Stopwatch.StartNew();
        _ = InternetFirewallLocalhostStoreCleanup.CleanupLegacyLocalhostArtifacts();
        GuardWatchdogLog.Append($"reconcile_step localhost_cleanup elapsed_ms={step.ElapsedMilliseconds}");

        step.Restart();
        var persisted = new InternetGuardStateStore().Load();
        var readback = InternetFirewallOffApplyReadback.Capture(config);
        GuardWatchdogLog.Append($"reconcile_step readback_capture elapsed_ms={step.ElapsedMilliseconds}");
        var model = readback.Model;
        var firewallLooksOff = model.ActiveStoreGuardRuleCount > 0 || model.AnyActiveProfileBlock;
        var statePresent = File.Exists(InternetGuardPaths.StatePath) && persisted is not null;

        if (firewallLooksOff && !statePresent)
        {
            step.Restart();
            var scriptResult = firewall.RunResetScript();
            GuardWatchdogLog.Append($"reconcile_step reset_script elapsed_ms={step.ElapsedMilliseconds}");
            string reconcileOutcome;
            if (!string.Equals(scriptResult, "firewall_script_ok", StringComparison.Ordinal))
            {
                reconcileOutcome = scriptResult;
            }
            else
            {
                step.Restart();
                var post = InternetFirewallOffApplyReadback.Capture(config);
                GuardWatchdogLog.Append($"reconcile_step post_reset_readback elapsed_ms={step.ElapsedMilliseconds}");
                reconcileOutcome = post.Model.ClassifyOnReset() ?? "applied";
            }

            GuardWatchdogLog.Append($"reconcile_reset={reconcileOutcome}");
        }

        GuardWatchdogLog.Append($"reconcile_steps_total elapsed_ms={total.ElapsedMilliseconds}");
    }
}