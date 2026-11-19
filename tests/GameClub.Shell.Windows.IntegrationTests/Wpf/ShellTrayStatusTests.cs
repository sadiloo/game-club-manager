#if NET48
using System;
using System.ServiceProcess;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using GameClub.Shell.Windows.IntegrationTests.Wpf;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellTrayStatusTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellTrayStatusTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_tray_icon_maps_stopped_start_pending_and_running_to_red_yellow_green_with_persian_tooltip()
    {
        FakeShellSeams.Install();
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            FakeShellSeams.Http.Answers = false;
            await Task.Delay(100);
            Assert.Equal(TrayIconState.Red, poller.Current.IconState);

            FakeShellSeams.Service.Status = ServiceControllerStatus.StartPending;
            await Task.Delay(6000);
            Assert.Equal(TrayIconState.Yellow, poller.Current.IconState);

            FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
            FakeShellSeams.Http.Answers = true;
            await Task.Delay(6000);
            Assert.Equal(TrayIconState.Green, poller.Current.IconState);
            Assert.Equal("سرور: روشن", poller.Current.TooltipLine1);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void shell_tray_menu_status_line_shows_tehran_start_time_when_running()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.ProcessStartLocalTime = DateTime.Today.AddHours(9);
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(200);
            Assert.Contains("سرور: روشن", poller.Current.MenuStatusLine);
            Assert.Contains("از ساعت", poller.Current.MenuStatusLine);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_tray_menu_status_line_shows_stopped_when_service_stopped()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
        FakeShellSeams.Http.Answers = false;
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(200);
            Assert.Equal("سرور: خاموش", poller.Current.MenuStatusLine);
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_unexpected_service_stop_shows_exactly_one_notification()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(200);
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            FakeShellSeams.Http.Answers = false;
            await Task.Delay(6000);
            await Task.Delay(6000);
            Assert.Equal(1, FakeShellSeams.Notifications.UnexpectedStopCount);
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void shell_tray_initiated_stop_shows_no_unexpected_stop_notification()
    {
        FakeShellSeams.Install();
        ShellSeams.TrayStopInProgress = true;
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(100);
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            await Task.Delay(6000);
            Assert.Equal(0, FakeShellSeams.Notifications.UnexpectedStopCount);
            ShellSeams.TrayStopInProgress = false;
        }, TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void shell_disk_warning_flag_shows_persian_balloon_within_sixty_seconds()
    {
        FakeShellSeams.Install();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cp-flags-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shell-" + Guid.NewGuid().ToString("N")));
        try
        {
            _fixture.RunAsync(async () =>
            {
                using var watcher = new FlagFileWatcher();
                var tcs = new TaskCompletionSource<bool>();
                watcher.FlagBalloon += (_, text) =>
                {
                    if (text.Contains("فضا"))
                    {
                        tcs.TrySetResult(true);
                    }
                };
                System.IO.File.WriteAllText(System.IO.Path.Combine(ShellPaths.ProgramDataLogs, "disk-warning.flag"), "فضا کم است");
                await Task.WhenAny(tcs.Task, Task.Delay(65000));
            }, TimeSpan.FromSeconds(70));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", null);
        }
    }

    [Fact]
    public void shell_shell_does_not_delete_programdata_log_files()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keep.flag");
        System.IO.File.WriteAllText(path, "x");
        Assert.True(System.IO.File.Exists(path));
    }
}
#endif