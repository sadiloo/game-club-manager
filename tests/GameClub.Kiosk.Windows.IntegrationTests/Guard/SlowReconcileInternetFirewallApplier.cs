#if NET48
using System;
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness — delays reconcile reset so pipe startup can be observed while reconcile runs.</summary>
internal sealed class SlowReconcileInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly int _resetDelayMs;

    internal SlowReconcileInternetFirewallApplier(int resetDelayMs) => _resetDelayMs = resetDelayMs;

    public string Apply(InternetGuardConfig config, bool on) => _inner.Apply(config, on);

    public string RunResetScript()
    {
        if (_resetDelayMs > 0)
        {
            var remaining = _resetDelayMs;
            while (remaining > 0)
            {
                if (InternetPowerShellRunner.IsReconcileAbandoned())
                {
                    return "firewall_script_skipped_abandon";
                }

                var slice = Math.Min(100, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        if (InternetPowerShellRunner.IsReconcileAbandoned())
        {
            return "firewall_script_skipped_abandon";
        }

        return _inner.RunResetScript();
    }
}
#endif