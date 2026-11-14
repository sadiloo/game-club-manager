#if NET48
using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

/// <summary>One WPF <see cref="Application"/> per test collection; all UI work must run via <see cref="RunAsync"/>.</summary>
public sealed class WpfStaApplicationFixture : IDisposable
{
    private readonly Thread _staThread;
    private Application _app;
    private Exception _dispatcherUnhandledException;

    public WpfStaApplicationFixture()
    {
        var ready = new ManualResetEventSlim(false);
        _staThread = new Thread(() =>
        {
            _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
            throw new InvalidOperationException("WPF Application STA thread did not become ready within 30 seconds.");
        }
    }

    public void RunAsync(Func<Task> body, TimeSpan timeout)
    {
        _dispatcherUnhandledException = null;
        var invokeOp = _app.Dispatcher.InvokeAsync(body);
        var task = invokeOp.Task.Unwrap();
        if (!task.Wait(timeout))
        {
            throw new TimeoutException($"WPF test body did not complete within {timeout}.");
        }

        if (_dispatcherUnhandledException is not null)
        {
            ExceptionDispatchInfo.Capture(_dispatcherUnhandledException).Throw();
        }

        if (task.IsFaulted)
        {
            throw task.Exception!.GetBaseException();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_app is not null)
            {
                _app.Dispatcher.Invoke(_app.Shutdown);
            }
        }
        catch
        {
            // never throw from Dispose
        }

        _staThread?.Join(TimeSpan.FromSeconds(30));
    }
}
#endif
