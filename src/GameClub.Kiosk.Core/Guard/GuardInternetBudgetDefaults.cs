namespace GameClub.Kiosk.Core.Guard;

/// <summary>Default PowerShell step budgets for Guard internet firewall (reconcile + apply pipe paths).</summary>
public static class GuardInternetBudgetDefaults
{
    public const int PowerShellKillAfterExitWaitMs = 3_000;
    public const int ReconcileWallTimeoutMs = 120_000;
    public const int ReconcilePlanMarginMs = 10_000;

    /// <summary>Reconcile orphan path: localhost cleanup (3× CI measured max 4178 ms).</summary>
    public const int ReconcileLocalhostCleanupMs = 13_000;
    /// <summary>Reconcile orphan path: initial readback capture (3× CI measured max 3967 ms).</summary>
    public const int ReconcileReadbackCaptureMs = 12_000;
    /// <summary>Reconcile orphan path: reset script.</summary>
    public const int ReconcileResetScriptMs = 28_000;
    /// <summary>Reconcile orphan path: post-reset readback (3× planning parity with readback capture).</summary>
    public const int ReconcilePostResetReadbackMs = 12_000;

    /// <summary>Apply path (each pipe apply and reassert tail).</summary>
    public const int ApplyLocalhostCleanupMs = 4_000;
    public const int ApplyFirewallScriptMs = 8_000;
    public const int ApplyReadbackCaptureMs = 8_000;

    /// <summary>
    /// Longest reconcile path: steps 1–4, then reassert <c>Apply</c> (cleanup + reset/off script + readback).
    /// </summary>
    public const int ReconcilePathPowerShellCallCountWorstCase = 7;

    /// <summary>Longest single pipe apply (cleanup + script + readback when turning OFF).</summary>
    public const int ApplyPathPowerShellCallCountWorstCase = 3;

    public static int ReconcilePowerShellBudgetSumMs()
        => ReconcileLocalhostCleanupMs
           + ReconcileReadbackCaptureMs
           + ReconcileResetScriptMs
           + ReconcilePostResetReadbackMs
           + ApplyPathPowerShellBudgetSumMs();

    public static int ApplyPathPowerShellBudgetSumMs()
        => ApplyLocalhostCleanupMs + ApplyFirewallScriptMs + ApplyReadbackCaptureMs;

    public static int ReconcileWorstCaseWithKillWaitsMs()
        => ReconcilePowerShellBudgetSumMs()
           + (ReconcilePathPowerShellCallCountWorstCase * PowerShellKillAfterExitWaitMs);

    public static int ApplyPathWorstCaseWithKillWaitsMs()
        => ApplyPathPowerShellBudgetSumMs()
           + (ApplyPathPowerShellCallCountWorstCase * PowerShellKillAfterExitWaitMs);

    /// <summary>Client pipe gate wait during reconcile — not stacked with a full apply in one kiosk IO window.</summary>
    public static bool ApplyGateWaitFitsClientIo()
        => GuardInternetClient.DefaultServerApplyWaitMs < GuardInternetClient.DefaultIoTimeoutMs;

    public static bool ApplyPathWorstCaseFitsClientIo()
        => ApplyPathWorstCaseWithKillWaitsMs() < GuardInternetClient.DefaultIoTimeoutMs;

    public static bool ReconcileWorstCaseFitsWallWithMargin()
        => ReconcileWorstCaseWithKillWaitsMs() + ReconcilePlanMarginMs <= ReconcileWallTimeoutMs;
}