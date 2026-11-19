using System;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardReconcilePolicy
{
    internal const int DefaultReconcileTimeoutMs = 120_000;
    internal const int DefaultApplyWaitDuringReconcileMs = GuardInternetClient.DefaultServerApplyWaitMs;

    /// <summary>Per-step budgets along the reconcile path (sum must stay below <see cref="DefaultReconcileTimeoutMs"/>).</summary>
    internal const int DefaultLocalhostCleanupBudgetMs = 15_000;
    internal const int DefaultReadbackCaptureBudgetMs = 15_000;
    internal const int DefaultResetScriptBudgetMs = 45_000;
    internal const int DefaultPostResetReadbackBudgetMs = 15_000;
    internal const int DefaultReassertApplyBudgetMs = 15_000;

    /// <summary>Apply-path PowerShell reset (must fit under client IO minus apply wait slack).</summary>
    internal const int DefaultApplyResetScriptBudgetMs = 25_000;
    internal const int DefaultApplyOffScriptBudgetMs = 25_000;
    internal const int DefaultApplyReadbackBudgetMs = 15_000;
    internal const int DefaultApplyLocalhostCleanupBudgetMs = 10_000;

    internal static int ResolveReconcileTimeoutMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_RECONCILE_TIMEOUT_MS");
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : DefaultReconcileTimeoutMs;
    }

    internal static int ResolveApplyWaitDuringReconcileMs()
    {
        var raw = Environment.GetEnvironmentVariable("CHECKPOINT_INTERNET_APPLY_WAIT_RECONCILE_MS");
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : DefaultApplyWaitDuringReconcileMs;
    }

    internal static int ResolveLocalhostCleanupBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_RECONCILE_LOCALHOST_CLEANUP_MS", DefaultLocalhostCleanupBudgetMs);

    internal static int ResolveReadbackCaptureBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_RECONCILE_READBACK_MS", DefaultReadbackCaptureBudgetMs);

    internal static int ResolveResetScriptBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_RECONCILE_RESET_MS", DefaultResetScriptBudgetMs);

    internal static int ResolvePostResetReadbackBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_RECONCILE_POST_RESET_READBACK_MS", DefaultPostResetReadbackBudgetMs);

    internal static int ResolveReassertApplyBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_RECONCILE_REASSERT_MS", DefaultReassertApplyBudgetMs);

    internal static int ResolveApplyResetScriptBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_APPLY_RESET_MS", DefaultApplyResetScriptBudgetMs);

    internal static int ResolveApplyOffScriptBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_APPLY_OFF_SCRIPT_MS", DefaultApplyOffScriptBudgetMs);

    internal static int ResolveApplyReadbackBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_APPLY_READBACK_MS", DefaultApplyReadbackBudgetMs);

    internal static int ResolveApplyLocalhostCleanupBudgetMs()
        => ResolveBudgetMs("CHECKPOINT_INTERNET_APPLY_LOCALHOST_CLEANUP_MS", DefaultApplyLocalhostCleanupBudgetMs);

    internal static int ReconcileStepBudgetTotalMs()
        => ResolveLocalhostCleanupBudgetMs()
           + ResolveReadbackCaptureBudgetMs()
           + ResolveResetScriptBudgetMs()
           + ResolvePostResetReadbackBudgetMs()
           + ResolveReassertApplyBudgetMs();

    internal static bool ApplyWaitBudgetIsBelowClientIoTimeout()
        => ResolveApplyWaitDuringReconcileMs() < GuardInternetClient.DefaultIoTimeoutMs;

    private static int ResolveBudgetMs(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : fallback;
    }
}