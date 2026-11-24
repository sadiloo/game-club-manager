#if NET48
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Test harness only — simulates Guard apply failure without production env hooks.</summary>
internal sealed class FaultInjectingInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (!on)
        {
            return "apply_failed:harness_injected";
        }

        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif