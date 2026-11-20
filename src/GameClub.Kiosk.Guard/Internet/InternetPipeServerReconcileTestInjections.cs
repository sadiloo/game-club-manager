using System.Threading;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Test-only reconcile hooks for <see cref="InternetPipeServer"/> (InternalsVisibleTo integration tests).</summary>
internal sealed class InternetPipeServerReconcileTestInjections
{
    internal ManualResetEventSlim? HangBeforeLocalhostCleanup { get; }

    internal InternetPipeServerReconcileTestInjections(ManualResetEventSlim? hangBeforeLocalhostCleanup = null)
    {
        HangBeforeLocalhostCleanup = hangBeforeLocalhostCleanup;
    }

    internal static InternetPipeServerReconcileTestInjections None { get; } = new();
}