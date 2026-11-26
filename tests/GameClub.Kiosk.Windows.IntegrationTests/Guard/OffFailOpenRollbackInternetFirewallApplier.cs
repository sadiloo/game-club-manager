#if NET48
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>Harness — OFF apply fails once; tracks ON restore calls for fail-open rollback tests.</summary>
internal sealed class OffFailOpenRollbackInternetFirewallApplier : IInternetFirewallApplier
{
    private readonly InternetFirewallApplier _inner = new();

    public int OffApplyCount { get; private set; }

    public int OnRestoreCount { get; private set; }

    public string Apply(InternetGuardConfig config, bool on)
    {
        if (!on)
        {
            OffApplyCount++;
            return "apply_failed:verification:test_harness";
        }

        OnRestoreCount++;
        return _inner.Apply(config, on);
    }

    public string RunResetScript() => _inner.RunResetScript();
}
#endif