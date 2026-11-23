#if NET48
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness — blocks inside reset script until the test cancels (reconcile timeout path).</summary>
internal sealed class HungReconcileInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly ManualResetEventSlim _release = new(false);

    internal void Release() => _release.Set();

    public string Apply(InternetGuardConfig config, bool on) => _inner.Apply(config, on);

    public string RunResetScript()
    {
        _release.Wait();
        return _inner.RunResetScript();
    }
}
#endif