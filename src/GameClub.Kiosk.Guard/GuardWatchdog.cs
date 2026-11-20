using System;
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

        if (File.Exists(InternetGuardPaths.ConfigPath))
        {
            try
            {
                var config = JsonSerializer.Deserialize<InternetGuardConfig>(File.ReadAllText(InternetGuardPaths.ConfigPath))
                    ?? new InternetGuardConfig();
                var pipe = new InternetPipeServer(config);
                var internetThread = new Thread(() =>
                {
                    try
                    {
                        pipe.Run(ct);
                    }
                    catch (Exception ex)
                    {
                        GuardWatchdogLog.Append($"internet_pipe_run_failed error={ex.Message}");
                        InternetGuardShellLaunchGate.MarkReady();
                    }
                })
                {
                    IsBackground = true,
                    Name = "CheckpointGuardInternet",
                };
                internetThread.Start();
            }
            catch
            {
                InternetGuardShellLaunchGate.MarkReady();
            }
        }
        else
        {
            InternetGuardShellLaunchGate.MarkReady();
        }

        new KioskShellSupervisor(identity).Run(ct);
    }
}
