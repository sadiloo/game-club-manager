using System.Diagnostics;
using System.ServiceProcess;

namespace GameClub.Kiosk.Guard;

internal static class Program
{
    private static void Main()
    {
        if (Environment.UserInteractive)
        {
            RunWatchdogLoop();
            return;
        }

        ServiceBase.Run(new GuardService());
    }

    internal static void RunWatchdogLoop()
    {
        while (true)
        {
            var kiosk = Process.GetProcessesByName("GameClub.Kiosk");
            if (kiosk.Length == 0)
            {
                TryRestartKiosk();
            }

            Thread.Sleep(TimeSpan.FromSeconds(2));
        }
    }

    private static void TryRestartKiosk()
    {
        var path = Environment.GetEnvironmentVariable("CHECKPOINT_KIOSK_EXE");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

internal sealed class GuardService : ServiceBase
{
    private Thread? _thread;

    protected override void OnStart(string[] args)
    {
        _thread = new Thread(Program.RunWatchdogLoop) { IsBackground = true };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _thread = null;
    }
}
