using System;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Single source for pipe-server handler join and harness teardown budgets (PR40-R28).</summary>
internal static class InternetPipeServerShutdownBudgets
{
    internal const int HandlerJoinBudgetSeconds = 10;
    internal const int TeardownMarginSeconds = 5;

    internal const int CancelledBackgroundJoinMs = 2_000;

    /// <summary>Drift, reconcile coordinator, and reconcile_work joins on cancel.</summary>
    internal const int CancelledBackgroundThreadJoinCount = 3;

    internal static TimeSpan HandlerJoinBudget => TimeSpan.FromSeconds(HandlerJoinBudgetSeconds);

    internal static int CancelledServerStopJoinMs =>
        (CancelledBackgroundThreadJoinCount * CancelledBackgroundJoinMs) + (int)HandlerJoinBudget.TotalMilliseconds;

    internal static TimeSpan CancelledServerStopJoinBudget =>
        TimeSpan.FromMilliseconds(CancelledServerStopJoinMs);

    internal static TimeSpan PipeThreadTeardownBudget =>
        CancelledServerStopJoinBudget + TimeSpan.FromSeconds(TeardownMarginSeconds);
}