#if NET48
#nullable disable
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Wraps applier Apply(OFF) with a harness delay to exercise held pipe requests during early reassert.</summary>
internal sealed class DelayedEarlyReassertInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly int _offApplyDelayMs;

    internal DelayedEarlyReassertInternetFirewallApplier(int offApplyDelayMs) =>
        _offApplyDelayMs = offApplyDelayMs;

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (!on && _offApplyDelayMs > 0)
        {
            Thread.Sleep(_offApplyDelayMs);
        }

        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif