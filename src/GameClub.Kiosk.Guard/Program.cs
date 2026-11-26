using System.Diagnostics;
using System.ServiceProcess;

namespace GameClub.Kiosk.Guard;

internal static class Program
{
    private static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (string.Equals(args[0], "internet-configure", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(Internet.InternetGuardCli.RunConfigure(args));
                return;
            }

            if (string.Equals(args[0], "internet-reset", StringComparison.OrdinalIgnoreCase))
            {
                Environment.Exit(Internet.InternetGuardCli.RunReset());
                return;
            }
        }

        if (Environment.UserInteractive)
        {
            RunWatchdogLoop();
            return;
        }

        ServiceBase.Run(new GuardService());
    }

    internal static void RunWatchdogLoop()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            Internet.InternetPowerShellRunner.RequestKillInFlight("process_exit");
        GuardWatchdog.Run(CancellationToken.None);
    }
}

internal sealed class GuardService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Thread? _thread;

    protected override void OnStart(string[] args)
    {
        var identity = KioskGuardIdentity.Resolve();
        if (!identity.IsResolved)
        {
            // R10j: fail visibly (log + non-zero exit) instead of a silent self-stop; kiosk-postinstall then fails with 150.
            GuardWatchdogLog.Append($"guard.service_identity_unresolved user={identity.UserName}");
            ExitCode = 1;
            Stop();
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() =>
        {
            try
            {
                GuardWatchdog.Run(token);
            }
            catch (Exception ex)
            {
                // R10j: record why the watchdog died before the process terminates (SCM then reports Stopped).
                GuardWatchdogLog.Append($"guard.watchdog_failed error={ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }) { IsBackground = true, Name = "CheckpointGuard" };
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
