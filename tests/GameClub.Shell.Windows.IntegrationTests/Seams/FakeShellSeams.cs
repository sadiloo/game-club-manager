#if NET48
using System;
using System.ServiceProcess;
using GameClub.Shell.Shell;

namespace GameClub.Shell.Windows.IntegrationTests.Seams;

internal sealed class FakeServiceSeam : IServiceControllerSeam
{
    public bool ServiceExists { get; set; } = true;
    public ServiceControllerStatus Status { get; set; } = ServiceControllerStatus.Running;
    public DateTime? ProcessStartLocalTime { get; set; } = DateTime.Now.AddHours(-1);
    public void Start() => Status = ServiceControllerStatus.Running;
    public void Stop() => Status = ServiceControllerStatus.Stopped;
    public bool WaitForStatus(ServiceControllerStatus status, TimeSpan timeout) => true;
}

internal sealed class FakeNotificationSeam : IShellNotificationSeam
{
    public int UnexpectedStopCount { get; private set; }
    public int NoAnswerStopCount { get; private set; }
    public string? LastUnexpectedStopTitle { get; private set; }
    public string? LastUnexpectedStopBody { get; private set; }

    public void ShowUnexpectedStop(bool noAnswer)
    {
        if (noAnswer)
        {
            NoAnswerStopCount++;
            LastUnexpectedStopTitle = "سرور پاسخ نمی‌دهد";
            LastUnexpectedStopBody = "از منوی کنار ساعت «توقف سرور» و بعد «شروع سرور» را بزنید.";
        }
        else
        {
            UnexpectedStopCount++;
            LastUnexpectedStopTitle = "سرور چک‌پوینت متوقف شد";
            LastUnexpectedStopBody = "از منوی کنار ساعت «شروع سرور» را بزنید.";
        }
    }

    public void ResetCounts()
    {
        UnexpectedStopCount = 0;
        NoAnswerStopCount = 0;
    }

    public void ShowBalloon(string title, string text) { }
}

internal sealed class FakeHttpSeam : IHttpProbeSeam
{
    public bool Answers { get; set; }
    public bool TryGetOperatorOk(out int statusCode)
    {
        statusCode = Answers ? 200 : 503;
        return Answers;
    }
}

internal static class FakeShellSeams
{
    public static FakeServiceSeam Service { get; } = new();
    public static FakeNotificationSeam Notifications { get; } = new();
    public static FakeHttpSeam Http { get; } = new();

    public static void RestoreProduction()
    {
        ShellSeams.Service = new ProductionServiceControllerSeam();
        ShellSeams.Notifications = new ProductionNotificationSeam();
        ShellSeams.HttpProbe = new ProductionHttpProbeSeam();
        ShellSeams.ReadLocalStatus = null;
        ShellSeams.ReadOpenSessionCount = null;
        ShellSeams.MessageBoxHandler = null;
        ShellSeams.MaintenanceFlagExists = null;
        ShellSeams.BeforeBuildSnapshot = null;
        ShellSeams.OnTickEntered = null;
        ShellSeams.OnTickSkippedReentrant = null;
        ShellSeams.TrayStopInProgressTrace = null;
        ShellSeams.OnPollerTickTrace = null;
        ShellSeams.StartElevatedProcess = null;
        ShellSeams.TrayStopObserved = false;
        ShellSeams.UtcNow = () => DateTime.UtcNow;
        ShellSeams.SystemUptime = () => TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
    }

    public static void ResetHarnessStatics()
    {
        Notifications.ResetCounts();
        ShellSeams.SetTrayStopInProgress(false, "FakeShellSeams.ResetHarnessStatics");
        ShellSeams.MessageBoxHandler = null;
        ShellSeams.BeforeBuildSnapshot = null;
        ShellSeams.OnTickEntered = null;
        ShellSeams.OnTickSkippedReentrant = null;
        ShellSeams.StartElevatedProcess = null;
        ShellSeams.TrayStopObserved = false;
    }

    public static void Install()
    {
        ResetHarnessStatics();
        ShellTrayDiagnosticTrace.Wire();
        ShellSeams.Service = Service;
        ShellSeams.Notifications = Notifications;
        ShellSeams.HttpProbe = Http;
        ShellSeams.ReadLocalStatus = () => new LocalServerStatusDto
        {
            DbOk = Http.Answers,
            StartedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
        Notifications.ResetCounts();
        Service.Status = ServiceControllerStatus.Running;
        Http.Answers = true;
        ShellSeams.UtcNow = () => DateTime.UtcNow;
        ShellSeams.SystemUptime = () => TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
        ShellSeams.MaintenanceFlagExists = null;
        ShellSeams.MessageBoxHandler = null;
        ShellSeams.ReadOpenSessionCount = null;
        ShellSeams.BeforeBuildSnapshot = null;
        ShellSeams.OnTickEntered = null;
        ShellSeams.OnTickSkippedReentrant = null;
    }
}
#endif