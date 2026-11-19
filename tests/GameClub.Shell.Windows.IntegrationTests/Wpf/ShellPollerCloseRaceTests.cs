#if NET48
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using GameClub.Shell;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellPollerCloseRaceTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellPollerCloseRaceTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_poller_tick_completing_after_main_window_close_does_not_touch_tray_or_panel()
    {
        var buildGate = new ManualResetEventSlim(false);
        var buildEntered = new ManualResetEventSlim(false);
        var domainUnhandled = 0;
        var dispatcherUnhandled = 0;
        var updateSnapshotAfterClose = 0;
        var closedFlag = 0;

        void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogShellRace(
                    "domain-unhandled",
                    $"type={ex.GetType().FullName} message={ex.Message} stack={ex.StackTrace}");
            }

            Interlocked.Increment(ref domainUnhandled);
        }

        void OnDispatcherUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            LogShellRace(
                "dispatcher-unhandled",
                $"type={e.Exception.GetType().FullName} message={e.Exception.Message} stack={e.Exception.StackTrace}");
            Interlocked.Increment(ref dispatcherUnhandled);
        }

        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandled;

        try
        {
            _fixture.RunAsync(async () =>
            {
                FakeShellSeams.Install();
                FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
                FakeShellSeams.Http.Answers = true;
                ShellSeams.BeforeBuildSnapshot = () =>
                {
                    buildEntered.Set();
                    buildGate.Wait(TimeSpan.FromSeconds(30));
                };

                var window = new MainWindow();
                window.Show();
                window.Hide();

                AttachPollerChangedProbe(
                    window,
                    snap => LogShellRace(
                        "poller-changed",
                        $"visual={snap.VisualState} operatorAnswers={snap.OperatorAnswers}"));

                await Task.Run(() =>
                {
                    if (!buildEntered.Wait(TimeSpan.FromSeconds(30)))
                    {
                        throw new System.TimeoutException("Timed out waiting for poller tick to enter BuildSnapshot.");
                    }
                });

                var trayHost = GetTrayHost(window);
                ToolStripMenuItem? statusLine = null;
                if (trayHost is not null)
                {
                    statusLine = GetStatusLine(trayHost);
                    HookStatusLineForPostCloseUpdates(statusLine, () => Volatile.Read(ref closedFlag), () => Interlocked.Increment(ref updateSnapshotAfterClose));
                }

                var navBeforeClose = window.NavigationStartingCount;
                var logBeforeClose = ReadShellLogTail();

                window.Closed += (_, _) => LogShellRace("window-closed-event", "");

                Volatile.Write(ref closedFlag, 1);
                ShellEnvironment.ForceExit = true;
                LogShellRace("before-window-close", "");
                window.Close();
                LogShellRace("after-window-close", "");
                ShellEnvironment.ForceExit = false;

                LogPollerCloseFlags("after-close-immediate", window);

                LogShellRace("before-buildGate-set", "");
                buildGate.Set();
                LogShellRace("after-buildGate-set", "");

                await Task.Delay(TimeSpan.FromSeconds(3));
                LogPollerCloseFlags("after-close-plus-3s", window);

                var lastLoggedNav = navBeforeClose;
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(100);
                    var navNow = window.NavigationStartingCount;
                    if (navNow > lastLoggedNav)
                    {
                        LogShellRace(
                            "navigation-after-close",
                            $"navCount={navNow} delta={navNow - navBeforeClose} stack={Environment.StackTrace}");
                        lastLoggedNav = navNow;
                    }
                }

                var logAfterClose = ReadShellLogTail();
                var navDelta = window.NavigationStartingCount - navBeforeClose;
                var panelNavigateAfterClose = logAfterClose
                    .Replace(logBeforeClose, "")
                    .Contains("panel-navigate", StringComparison.Ordinal);
                if (panelNavigateAfterClose)
                {
                    LogShellRace("panel-navigate-after-close", $"stack={Environment.StackTrace}");
                }

                var du = Volatile.Read(ref domainUnhandled);
                var disp = Volatile.Read(ref dispatcherUnhandled);
                var trayUpdates = Volatile.Read(ref updateSnapshotAfterClose);
                Console.WriteLine(
                    $"SHELL-RACE counters-pre-assert UTC={DateTime.UtcNow:O} thread={Thread.CurrentThread.ManagedThreadId} "
                    + $"dispatcher={OnDispatcherThread()} domainUnhandled={du} dispatcherUnhandled={disp} "
                    + $"updateSnapshotAfterClose={trayUpdates} navDelta={navDelta} panelNavigateAfterClose={panelNavigateAfterClose}");

                AssertZero(du, "AppDomain.UnhandledException fired during poller-close race");
                AssertZero(disp, "Dispatcher.UnhandledException fired during poller-close race");
                AssertZero(trayUpdates, "Tray status line updated after MainWindow close");
                AssertZero(navDelta, "NavigationStarting fired after MainWindow close");
                Assert.False(panelNavigateAfterClose, "panel-navigate log line appeared after window close");
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            System.Windows.Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandled;
            ShellSeams.BeforeBuildSnapshot = null;
            buildGate.Set();
        }
    }

    private static void AssertZero(int actual, string message)
    {
        if (actual != 0)
        {
            throw new Xunit.Sdk.XunitException($"{message} (actual={actual}).");
        }

        Assert.Equal(0, actual);
    }

    private static void LogPollerCloseFlags(string phase, MainWindow window)
    {
        var mainClosed = ReadMainWindowClosed(window);
        var pollerDisposed = ReadPollerDisposed(window);
        LogShellRace(
            $"flags-{phase}",
            $"MainWindow._closed={mainClosed} ServerStatePoller._disposed={pollerDisposed}");
    }

    private static bool ReadMainWindowClosed(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_closed", BindingFlags.Instance | BindingFlags.NonPublic);
        return field is not null && (bool)field.GetValue(window)!;
    }

    private static bool ReadPollerDisposed(MainWindow window)
    {
        var poller = GetPoller(window);
        if (poller is null)
        {
            return false;
        }

        var field = typeof(ServerStatePoller).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic);
        return field is not null && (bool)field.GetValue(poller)!;
    }

    private static ServerStatePoller? GetPoller(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_poller", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(window) as ServerStatePoller;
    }

    private static void AttachPollerChangedProbe(MainWindow window, Action<ServerStateSnapshot> handler)
    {
        var poller = GetPoller(window);
        if (poller is null)
        {
            return;
        }

        var add = typeof(ServerStatePoller).GetEvent("Changed", BindingFlags.Instance | BindingFlags.Public)!
            .GetAddMethod(nonPublic: true)!;
        add.Invoke(poller, new object[] { handler });
    }

    private static bool OnDispatcherThread() =>
        System.Windows.Application.Current?.Dispatcher?.CheckAccess() == true;

    private static void LogShellRace(string tag, string detail)
    {
        Console.WriteLine(
            $"SHELL-RACE tag={tag} UTC={DateTime.UtcNow:O} thread={Thread.CurrentThread.ManagedThreadId} "
            + $"dispatcher={OnDispatcherThread()} {detail}");
    }

    private static object? GetTrayHost(MainWindow window)
    {
        var field = typeof(MainWindow).GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(window);
    }

    private static ToolStripMenuItem GetStatusLine(object trayHost)
    {
        var field = trayHost.GetType().GetField("_statusLine", BindingFlags.Instance | BindingFlags.NonPublic);
        return (ToolStripMenuItem)field!.GetValue(trayHost)!;
    }

    private static void HookStatusLineForPostCloseUpdates(
        ToolStripMenuItem statusLine,
        Func<int> closedFlagReader,
        Action recordUpdateAfterClose)
    {
        statusLine.TextChanged += (_, _) =>
        {
            if (closedFlagReader() != 0)
            {
                LogShellRace("tray-status-line-after-close", $"stack={Environment.StackTrace}");
                recordUpdateAfterClose();
            }
        };
    }

    private static string ReadShellLogTail()
    {
        try
        {
            if (!Directory.Exists(ShellPaths.ShellLogsDir))
            {
                return "";
            }

            return Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                .Select(File.ReadAllText)
                .Aggregate("", string.Concat);
        }
        catch
        {
            return "";
        }
    }
}
#endif