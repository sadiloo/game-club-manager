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
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _thread = new Thread(() => GuardWatchdog.Run(_cts.Token)) { IsBackground = true, Name = "CheckpointGuard" };
        _thread.Start();
    }

    protected override void OnStop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _cts?.Dispose();
        _cts = null;
        _thread = null;
    }
}
