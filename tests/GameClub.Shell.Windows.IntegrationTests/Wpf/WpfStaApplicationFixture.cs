#if NET48
using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GameClub.Shell;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

public sealed class WpfStaApplicationFixture : IDisposable
{
    private readonly Thread _staThread;
    private Application _app = null!;
    private Exception? _dispatcherUnhandledException;

    public WpfStaApplicationFixture()
    {
        var ready = new ManualResetEventSlim(false);
        _staThread = new Thread(() =>
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
            _app.DispatcherUnhandledException += (_, e) =>
            {
                _dispatcherUnhandledException = e.Exception;
                e.Handled = true;
            };
            ready.Set();
            _app.Run();
        });
        _staThread.SetApartmentState(ApartmentState.STA);
        _staThread.IsBackground = true;
        _staThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("WPF Application STA thread did not become ready.");
        }
    }

    public void RunAsync(Func<Task> body, TimeSpan timeout)
    {
        _dispatcherUnhandledException = null;
        FakeShellSeams.ResetHarnessStatics();
        CloseAllWindows();
        var invokeOp = _app.Dispatcher.InvokeAsync(body);
        var task = invokeOp.Task.Unwrap();
        try
        {
            if (!task.Wait(timeout))
            {
                CloseAllWindows();
                throw new TimeoutException($"WPF test body did not complete within {timeout}.");
            }
        }
        catch
        {
            CloseAllWindows();
            throw;
        }

        CloseAllWindows();

        if (_dispatcherUnhandledException is not null)
        {
            ExceptionDispatchInfo.Capture(_dispatcherUnhandledException).Throw();
        }

        if (task.IsFaulted)
        {
            throw task.Exception!.GetBaseException();
        }
    }

    private void CloseAllWindows()
    {
        try
        {
            _app?.Dispatcher.Invoke(() =>
            {
                ShellEnvironment.ForceExit = true;
                try
                {
                    foreach (var window in _app.Windows.OfType<Window>().ToList())
                    {
                        if (window is MainWindow mw)
                        {
                            mw.ExitShell();
                            continue;
                        }

                        window.Close();
                    }
                }
                finally
                {
                    ShellEnvironment.ForceExit = false;
                }
            });
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        try
        {
            _app?.Dispatcher.Invoke(_app.Shutdown);
        }
        catch
        {
        }

        _staThread?.Join(TimeSpan.FromSeconds(30));
    }
}
#endif