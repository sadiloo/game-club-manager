using System;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardReconcilePolicy
{
    internal const int DefaultReconcileTimeoutMs = GuardInternetBudgetDefaults.ReconcileWallTimeoutMs;
    internal const int DefaultApplyWaitDuringReconcileMs = GuardInternetClient.DefaultServerApplyWaitMs;

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
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_RECONCILE_LOCALHOST_CLEANUP_MS",
            GuardInternetBudgetDefaults.ReconcileLocalhostCleanupMs);

    internal static int ResolveReadbackCaptureBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_RECONCILE_READBACK_MS",
            GuardInternetBudgetDefaults.ReconcileReadbackCaptureMs);

    internal static int ResolveResetScriptBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_RECONCILE_RESET_MS",
            GuardInternetBudgetDefaults.ReconcileResetScriptMs);

    internal static int ResolvePostResetReadbackBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_RECONCILE_POST_RESET_READBACK_MS",
            GuardInternetBudgetDefaults.ReconcilePostResetReadbackMs);

    internal static int ResolveApplyMergedBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_APPLY_MERGED_MS",
            GuardInternetBudgetDefaults.ApplyMergedPowerShellBudgetMs);

    internal static int ResolveApplyReadbackBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_APPLY_READBACK_MS",
            GuardInternetBudgetDefaults.ReconcileReadbackCaptureMs);

    internal static int ResolveApplyResetScriptBudgetMs()
        => ResolveBudgetMs(
            "CHECKPOINT_INTERNET_APPLY_RESET_MS",
            GuardInternetBudgetDefaults.ReconcileResetScriptMs);

    internal static bool ApplyWaitBudgetIsBelowClientIoTimeout()
        => ResolveApplyWaitDuringReconcileMs() < GuardInternetClient.DefaultIoTimeoutMs;

    private static int ResolveBudgetMs(string envVar, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(envVar);
        return int.TryParse(raw, out var ms) && ms > 0 ? ms : fallback;
    }
}