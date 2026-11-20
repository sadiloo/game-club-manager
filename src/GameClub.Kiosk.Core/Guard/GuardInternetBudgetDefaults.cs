namespace GameClub.Kiosk.Core.Guard;

/// <summary>Default PowerShell step budgets for Guard internet firewall (reconcile + apply pipe paths).</summary>
public static class GuardInternetBudgetDefaults
{
    public const int PowerShellKillAfterExitWaitMs = 3_000;
    public const int ReconcileWallTimeoutMs = 120_000;
    public const int ReconcilePlanMarginMs = 10_000;

    /// <summary>CI measured max (run 44567958282 watchdog).</summary>
    public const int MeasuredReconcileLocalhostCleanupMs = 4_178;
    /// <summary>CI measured max (run 44567958282 watchdog).</summary>
    public const int MeasuredReconcileReadbackCaptureMs = 3_967;
    /// <summary>CI measured apply/reassert tail wall (reconcile_end − reconcile_steps_total).</summary>
    public const int MeasuredApplyMergedWallMs = 7_291;
    /// <summary>Reconcile reset script — no apply-only measure; reconcile-path planning analogue.</summary>
    public const int MeasuredReconcileResetScriptPlanningMs = 28_000;

    /// <summary>Reconcile orphan path: localhost cleanup (3× measured 4178).</summary>
    public const int ReconcileLocalhostCleanupMs = 13_000;
    /// <summary>Reconcile orphan path: initial readback capture (3× measured 3967).</summary>
    public const int ReconcileReadbackCaptureMs = 12_000;
    /// <summary>Reconcile orphan path: reset script (reconcile-path analogue, unchanged).</summary>
    public const int ReconcileResetScriptMs = 28_000;
    /// <summary>Reconcile orphan path: post-reset readback (3× readback measured).</summary>
    public const int ReconcilePostResetReadbackMs = 12_000;

    /// <summary>Single merged pipe apply + reassert tail (3× measured merged wall 7291).</summary>
    public const int ApplyMergedPowerShellBudgetMs = 22_000;

    /// <summary>Longest reconcile path: localhost, readback, reset, post-reset readback, merged reassert apply.</summary>
    public const int ReconcilePathPowerShellCallCountWorstCase = 5;

    /// <summary>Pipe apply uses one merged PowerShell process.</summary>
    public const int ApplyPathPowerShellCallCountWorstCase = 1;

    /// <summary>Early-reassert hold may queue this many mutating apply pipe requests (FIFO integration contract).</summary>
    public const int MutatingApplyFifoQueueDepthWorstCase = 2;

    /// <summary>Margin between client IO timeout and a single apply path worst-case budget.</summary>
    public const int ClientIoTimeoutMarginMs = 5_000;

    public static int ReconcilePowerShellBudgetSumMs()
        => ReconcileLocalhostCleanupMs
           + ReconcileReadbackCaptureMs
           + ReconcileResetScriptMs
           + ReconcilePostResetReadbackMs
           + ApplyMergedPowerShellBudgetMs;

    public static int ApplyPathPowerShellBudgetSumMs() => ApplyMergedPowerShellBudgetMs;

    public static int ReconcileWorstCaseWithKillWaitsMs()
        => ReconcilePowerShellBudgetSumMs()
           + (ReconcilePathPowerShellCallCountWorstCase * PowerShellKillAfterExitWaitMs);

    public static int ApplyPathWorstCaseWithKillWaitsMs()
        => ApplyPathPowerShellBudgetSumMs()
           + (ApplyPathPowerShellCallCountWorstCase * PowerShellKillAfterExitWaitMs);

    public static int MutatingApplyFifoClientIoBudgetMs()
        => (MutatingApplyFifoQueueDepthWorstCase * ApplyPathWorstCaseWithKillWaitsMs()) + ClientIoTimeoutMarginMs;

    public static bool ApplyGateWaitFitsClientIo()
        => GuardInternetClient.DefaultServerApplyWaitMs < GuardInternetClient.DefaultIoTimeoutMs;

    public static bool ApplyPathWorstCaseFitsClientIo()
        => ApplyPathWorstCaseWithKillWaitsMs() < GuardInternetClient.DefaultIoTimeoutMs;

    public static bool MutatingApplyFifoFitsClientIo()
        => MutatingApplyFifoClientIoBudgetMs() <= GuardInternetClient.DefaultIoTimeoutMs;

    public static bool ReconcileWorstCaseFitsWallWithMargin()
        => ReconcileWorstCaseWithKillWaitsMs() + ReconcilePlanMarginMs <= ReconcileWallTimeoutMs;
}