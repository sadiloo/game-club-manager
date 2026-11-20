using System;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Single source for pipe-server handler join and harness teardown budgets (PR40-R28).</summary>
internal static class InternetPipeServerShutdownBudgets
{
    internal const int HandlerJoinBudgetSeconds = 10;
    internal const int TeardownMarginSeconds = 5;

    internal static TimeSpan HandlerJoinBudget => TimeSpan.FromSeconds(HandlerJoinBudgetSeconds);

    internal static TimeSpan PipeThreadTeardownBudget =>
        TimeSpan.FromSeconds(HandlerJoinBudgetSeconds + TeardownMarginSeconds);

    internal const int CancelledBackgroundJoinMs = 2_000;
}