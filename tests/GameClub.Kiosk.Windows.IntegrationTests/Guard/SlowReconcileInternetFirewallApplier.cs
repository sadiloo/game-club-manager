#if NET48
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
            Thread.Sleep(_resetDelayMs);
        }

        return _inner.RunResetScript();
    }
}
#endif