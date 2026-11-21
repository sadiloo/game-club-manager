#if NET48
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness — delays apply on ON commands so status can be checked concurrently.</summary>
internal sealed class SlowApplyInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();
    private readonly int _applyOnDelayMs;
    private readonly ManualResetEventSlim _releaseDelay = new(false);

    internal SlowApplyInternetFirewallApplier(int applyOnDelayMs) => _applyOnDelayMs = applyOnDelayMs;

    internal void ReleaseApplyDelay() => _releaseDelay.Set();

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (on && _applyOnDelayMs > 0)
        {
            _releaseDelay.Wait(_applyOnDelayMs);
        }

        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif