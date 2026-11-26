using System.Threading;

namespace GameClub.Kiosk.Guard.Internet;

/// <summary>Blocks kiosk shell relaunch until internet pipe startup (early reassert) is far enough along.</summary>
internal static class InternetGuardShellLaunchGate
{
    private static int _ready;

    internal static void ResetReady() => Interlocked.Exchange(ref _ready, 0);

    internal static void MarkReady() => Interlocked.Exchange(ref _ready, 1);

    internal static bool IsReady => Volatile.Read(ref _ready) != 0;
}