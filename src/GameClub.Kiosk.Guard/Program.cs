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

    internal static void RunWatchdogLoop() => GuardWatchdog.Run(CancellationToken.None);
}

internal sealed class GuardService : ServiceBase
{
    private Thread? _thread;

    protected override void OnStart(string[] args)
    {
        var cts = new CancellationTokenSource();
        _thread = new Thread(() => GuardWatchdog.Run(cts.Token)) { IsBackground = true };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _thread = null;
    }
}
