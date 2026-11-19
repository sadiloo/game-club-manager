using System;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardReconcilePolicy
{
    private const int DefaultReconcileTimeoutMs = 120_000;
    private const int DefaultApplyWaitReconcileMs = 30_000;

    internal static int ResolveReconcileTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : DefaultReconcileTimeoutMs;
    }

    internal static int ResolveApplyWaitDuringReconcileMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_WAIT_RECONCILE_MS");
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : DefaultApplyWaitReconcileMs;
    }
}