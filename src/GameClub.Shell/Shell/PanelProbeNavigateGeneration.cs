using System.Threading;

namespace GameClub.Shell.Shell;

internal static class PanelProbeNavigateGeneration
{
    private static int _generation;

    public static int CaptureForProbe() => Volatile.Read(ref _generation);

    public static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
    }

    public static bool IsProbeNavigateStillValid(int captured) =>
        captured == Volatile.Read(ref _generation);
}