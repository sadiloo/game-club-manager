using System;
using System.Threading;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Harness-only hooks for Windows integration tests (not used in production).</summary>
internal static class InternetGuardReconcileTestSeams
{
    internal static ManualResetEventSlim? HangBeforeLocalhostCleanup;

    internal static void WaitHangBeforeLocalhostCleanupIfConfigured(Func<bool> shouldContinue)
    {
        var hang = HangBeforeLocalhostCleanup;
        if (hang is null)
        {
            return;
        }

        hang.Wait();
    }
}