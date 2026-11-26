#if NET48
using System;
using System.Linq;
using System.ServiceProcess;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using GameClub.Shell.Windows.IntegrationTests.Wpf;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellPollerFRulingTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellPollerFRulingTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_f1_stopped_red_start_stop_pending_yellow()
    {
        FakeShellSeams.Install();
        _fixture.RunAsync(() =>
        {
            using var poller = new ServerStatePoller();
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            FakeShellSeams.Http.Answers = false;
            ShellSeams.SystemUptime = () => TimeSpan.FromMinutes(10);
            var snap = poller.BuildSnapshotForTests();
            Assert.Equal(TrayIconState.Red, snap.IconState);
            Assert.Contains("خاموش", snap.MenuStatusLine);

            FakeShellSeams.Service.Status = ServiceControllerStatus.StartPending;
            snap = poller.BuildSnapshotForTests();
            Assert.Equal(TrayIconState.Yellow, snap.IconState);

            FakeShellSeams.Service.Status = ServiceControllerStatus.StopPending;
            snap = poller.BuildSnapshotForTests();
            Assert.Equal(TrayIconState.Yellow, snap.IconState);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_f4_delayed_auto_boot_under_five_minutes_shows_yellow()
    {
        FakeShellSeams.Install();
        _fixture.RunAsync(() =>
        {
            using var poller = new ServerStatePoller();
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            ShellSeams.SystemUptime = () => TimeSpan.FromMinutes(2);
            var snap = poller.BuildSnapshotForTests();
            Assert.Equal(TrayIconState.Yellow, snap.IconState);
            Assert.Contains("روشن", snap.MenuStatusLine);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_f5_running_without_db_ok_becomes_no_answer_red_with_distinct_text()
    {
        FakeShellSeams.Install();
        var now = DateTime.UtcNow;
        ShellSeams.UtcNow = () => now;
        _fixture.RunAsync(() =>
        {
            using var poller = new ServerStatePoller();
            FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
            FakeShellSeams.Http.Answers = false;
            _ = poller.BuildSnapshotForTests();
            ShellSeams.UtcNow = () => now.AddSeconds(95);
            var snap = poller.BuildSnapshotForTests();
            Assert.Equal(TrayIconState.Red, snap.IconState);
            Assert.Contains("بدون پاسخ", snap.MenuStatusLine);
            Assert.DoesNotContain("خاموش", snap.MenuStatusLine);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_f5_running_menu_shows_persian_yesterday_start_time()
    {
        FakeShellSeams.Install();
        var tehranZone = TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
        var yesterdayTehran = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tehranZone).Date.AddDays(-1).AddHours(9);
        var startedUtc = TimeZoneInfo.ConvertTimeToUtc(yesterdayTehran, tehranZone);
        ShellSeams.UtcNow = () => DateTime.UtcNow;
        ShellSeams.ReadLocalStatus = () => new LocalServerStatusDto { DbOk = true, StartedAtUtc = startedUtc };
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        _fixture.RunAsync(() =>
        {
            using var poller = new ServerStatePoller();
            var snap = poller.BuildSnapshotForTests();
            Assert.Contains("از دیروز", snap.MenuStatusLine);
            Assert.DoesNotContain("0", snap.MenuStatusLine);
            Assert.DoesNotContain("1", snap.MenuStatusLine);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void shell_f6_unexpected_stop_toast_has_no_latin_letters()
    {
        FakeShellSeams.Install();
        ShellSeams.Notifications.ShowUnexpectedStop(noAnswer: false);
        var stoppedTitle = FakeShellSeams.Notifications.LastUnexpectedStopTitle ?? "";
        var stoppedBody = FakeShellSeams.Notifications.LastUnexpectedStopBody ?? "";
        AssertNoLatinLetters(stoppedTitle);
        AssertNoLatinLetters(stoppedBody);
        ShellSeams.Notifications.ShowUnexpectedStop(noAnswer: true);
        var noAnswerTitle = FakeShellSeams.Notifications.LastUnexpectedStopTitle ?? "";
        var noAnswerBody = FakeShellSeams.Notifications.LastUnexpectedStopBody ?? "";
        AssertNoLatinLetters(noAnswerTitle);
        AssertNoLatinLetters(noAnswerBody);
        Assert.NotEqual(stoppedTitle, noAnswerTitle);
    }

    private static void AssertNoLatinLetters(string text)
    {
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                Assert.Fail($"Latin letter in Persian toast text: '{c}'");
            }
        }
    }

    [Fact]
    public void shell_f10_maintenance_flag_suppresses_unexpected_stop_toast()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        ShellSeams.MaintenanceFlagExists = _ => true;
        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(100);
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            FakeShellSeams.Http.Answers = false;
            await Task.Delay(6000);
            Assert.Equal(0, FakeShellSeams.Notifications.UnexpectedStopCount);
            ShellSeams.MaintenanceFlagExists = null;
        }, TimeSpan.FromSeconds(20));
    }
}
#endif