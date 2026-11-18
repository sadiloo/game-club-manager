using System.IO;
using System.Text.Json;
using System.Threading;
using GameClub.Kiosk.Guard.Internet;

namespace GameClub.Kiosk.Guard;

internal static class GuardWatchdog
{
    internal static void Run(CancellationToken ct)
    {
        var identity = KioskGuardIdentity.Resolve();
        if (!identity.IsResolved)
        {
            return;
        }

        Thread? internetThread = null;
        try
        {
            if (File.Exists(InternetGuardPaths.ConfigPath))
            {
                var config = JsonSerializer.Deserialize<InternetGuardConfig>(File.ReadAllText(InternetGuardPaths.ConfigPath))
                    ?? new InternetGuardConfig();
                var pipe = new InternetPipeServer(config);
                internetThread = new Thread(() => pipe.Run(ct)) { IsBackground = true, Name = "CheckpointGuardInternet" };
                internetThread.Start();
            }
        }
        catch
        {
        }

        new KioskShellSupervisor(identity).Run(ct);
    }
}
