using System;
using System.Threading;
using System.Windows;
using GameClub.Shell.ServerControl;
using GameClub.Shell.Shell;

namespace GameClub.Shell;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\CheckpointShell";
    private Mutex? _mutex;

    public static int RunWithArgs(string[] args)
    {
        if (args.Length >= 2 && string.Equals(args[0], "--server-control", StringComparison.OrdinalIgnoreCase))
        {
            return ServerControlProgram.Run(args[1]);
        }

        var app = new App();
        app.InitializeComponent();
        if (!app.TryBecomeSingleInstance())
        {
            return 0;
        }

        app.Run();
        return 0;
    }

    private bool TryBecomeSingleInstance()
    {
        var createdNew = false;
        _mutex = new Mutex(true, MutexName, out createdNew);
        if (createdNew)
        {
            Startup += (_, _) =>
            {
                MainWindow = new MainWindow();
                MainWindow.Show();
            };
            return true;
        }

        NativeMessaging.SignalExistingInstance();
        return false;
    }
}