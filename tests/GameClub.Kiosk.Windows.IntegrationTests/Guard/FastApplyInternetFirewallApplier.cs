#if NET48
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness-only applier that returns immediately (pipe framing tests).</summary>
internal sealed class FastApplyInternetFirewallApplier : IInternetFirewallApplier
{
    public string Apply(InternetGuardConfig config, bool on) => "applied";

    public string RunResetScript() => "applied";
}
#endif