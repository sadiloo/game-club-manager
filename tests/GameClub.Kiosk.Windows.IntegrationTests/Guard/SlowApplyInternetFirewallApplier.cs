#if NET48
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness — delays apply on ON commands so status can be checked concurrently.</summary>
internal sealed class SlowApplyInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly int _applyOnDelayMs;

    internal SlowApplyInternetFirewallApplier(int applyOnDelayMs) => _applyOnDelayMs = applyOnDelayMs;

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (on && _applyOnDelayMs > 0)
        {
            Thread.Sleep(_applyOnDelayMs);
        }

        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif