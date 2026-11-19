#if NET48
using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellPollerReentrancyTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellPollerReentrancyTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_poller_skips_reentrant_tick_during_slow_snapshot_and_one_unexpected_toast()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        var slowSnapshots = 0;
        var skipped = 0;
        ShellSeams.BeforeBuildSnapshot = () =>
        {
            if (Volatile.Read(ref slowSnapshots) > 0)
            {
                Thread.Sleep(7000);
            }
        };
        ShellSeams.OnTickSkippedReentrant = () => Interlocked.Increment(ref skipped);

        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(500);
            Interlocked.Exchange(ref slowSnapshots, 1);
            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            FakeShellSeams.Http.Answers = false;
            await Task.Delay(15000);
            Assert.True(skipped >= 1, $"expected at least one skipped reentrant tick; skipped={skipped}");
            Assert.Equal(1, FakeShellSeams.Notifications.UnexpectedStopCount);
        }, TimeSpan.FromSeconds(35));
    }

    [Fact]
    public void shell_no_answer_to_stopped_shows_exactly_one_toast()
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        var t0 = new DateTime(2026, 6, 3, 12, 0, 0, DateTimeKind.Utc);
        ShellSeams.UtcNow = () => t0;

        _fixture.RunAsync(async () =>
        {
            using var poller = new ServerStatePoller();
            await Task.Delay(500);
            FakeShellSeams.Http.Answers = false;
            _ = poller.BuildSnapshotForTests();
            ShellSeams.UtcNow = () => t0.AddSeconds(91);
            await Task.Delay(6000);
            Assert.Equal(1, FakeShellSeams.Notifications.NoAnswerStopCount);
            Assert.Equal(0, FakeShellSeams.Notifications.UnexpectedStopCount);

            FakeShellSeams.Service.Status = ServiceControllerStatus.Stopped;
            await Task.Delay(6000);
            Assert.Equal(1, FakeShellSeams.Notifications.NoAnswerStopCount);
            Assert.Equal(0, FakeShellSeams.Notifications.UnexpectedStopCount);
        }, TimeSpan.FromSeconds(35));
    }
}
#endif