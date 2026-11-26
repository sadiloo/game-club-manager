#if NET48
#nullable disable
using System;
using System.Diagnostics;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

/// <summary>CI harness — terminate stray GameClub Guard/Kiosk processes before live integration phases.</summary>
internal static class CiGuardKioskProcessCleanup
{
    internal static void KillOrphanedGuardAndKiosk(string reason)
    {
        foreach (var name in new[] { "GameClub.Kiosk.Guard", "GameClub.Kiosk" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    Console.WriteLine($"CI_ORPHAN_KILL reason={reason} name={name} pid={proc.Id}");
                    GuardProcessLifecycle.StopGuardProcessTree(proc, TimeSpan.FromSeconds(20));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"CI_ORPHAN_KILL_FAILED name={name} pid={proc.Id} error={ex.Message}");
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }
    }
}
#endif