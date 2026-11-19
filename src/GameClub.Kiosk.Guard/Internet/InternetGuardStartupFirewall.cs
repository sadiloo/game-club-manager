using System.IO;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Reconcile persistent-store firewall with verified state at Guard/pipe start (PR40-R15 iii).</summary>
internal static class InternetGuardStartupFirewall
{
    public static void ReconcileOnPipeStart(InternetGuardConfig config, IInternetFirewallApplier firewall)
    {
        InternetFirewallLocalhostStoreCleanup.CleanupLegacyLocalhostArtifacts();
        var persisted = new InternetGuardStateStore().Load();
        var readback = InternetFirewallOffApplyReadback.Capture(config);
        var model = readback.Model;
        var firewallLooksOff = model.ActiveStoreGuardRuleCount > 0 || model.AllActiveProfilesBlock;
        var statePresent = File.Exists(InternetGuardPaths.StatePath) && persisted is not null;

        if (firewallLooksOff && !statePresent)
        {
            firewall.RunResetScript();
        }
    }
}