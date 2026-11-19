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
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;

        var buildGate = new ManualResetEventSlim(false);
        var buildEntered = new ManualResetEventSlim(false);
        var domainUnhandled = 0;
        var dispatcherUnhandled = 0;
        var updateSnapshotAfterClose = 0;
        var closedFlag = 0;

        void OnDomainUnhandled(object sender, UnhandledExceptionEventArgs e) => Interlocked.Increment(ref domainUnhandled);
        void OnDispatcherUnhandled(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) =>
            Interlocked.Increment(ref dispatcherUnhandled);

        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandled;
        System.Windows.Application.Current.DispatcherUnhandledException += OnDispatcherUnhandled;
        ShellSeams.BeforeBuildSnapshot = () =>
        {
            buildEntered.Set();
            buildGate.Wait(TimeSpan.FromSeconds(30));
        };

        try
        {
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                window.Hide();

                await Task.Run(() =>
                {
                    if (!buildEntered.Wait(TimeSpan.FromSeconds(15)))
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

                Volatile.Write(ref closedFlag, 1);
                ShellEnvironment.ForceExit = true;
                window.Close();
                ShellEnvironment.ForceExit = false;

                buildGate.Set();
                await Task.Delay(3000);

                var logAfterClose = ReadShellLogTail();
                var navDelta = window.NavigationStartingCount - navBeforeClose;
                var panelNavigateAfterClose = logAfterClose
                    .Replace(logBeforeClose, "")
                    .Contains("panel-navigate", StringComparison.Ordinal);

                Assert.Equal(0, Volatile.Read(ref domainUnhandled));
                Assert.Equal(0, Volatile.Read(ref dispatcherUnhandled));
                Assert.Equal(0, Volatile.Read(ref updateSnapshotAfterClose));
                Assert.Equal(0, navDelta);
                Assert.False(panelNavigateAfterClose, "panel-navigate log line appeared after window close");
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            System.Windows.Application.Current.DispatcherUnhandledException -= OnDispatcherUnhandled;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandled;
            ShellSeams.BeforeBuildSnapshot = null;
            buildGate.Set();
        }
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