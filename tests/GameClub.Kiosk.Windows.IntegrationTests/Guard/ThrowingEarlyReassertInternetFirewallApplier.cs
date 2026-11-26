#if NET48
#nullable disable
using System;
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Throws on the first OFF apply only (early reassert) so later OFF pipe applies can succeed.</summary>
internal sealed class ThrowingEarlyReassertInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly string _offFailureMessage;
    private int _offApplyAttempts;

    internal ThrowingEarlyReassertInternetFirewallApplier(string offFailureMessage = "harness_early_reassert_off_failure")
        => _offFailureMessage = offFailureMessage;

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (!on && Interlocked.Increment(ref _offApplyAttempts) == 1)
        {
            throw new InvalidOperationException(_offFailureMessage);
        }

        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif