using System;
using System.Collections.Generic;
using System.ServiceProcess;
using System.Windows.Forms;

namespace GameClub.Shell.Shell;

public enum TrayIconState
{
    Green,
    Yellow,
    Red,
}

public interface IServiceControllerSeam
{
    bool ServiceExists { get; }
    ServiceControllerStatus Status { get; }
    DateTime? ProcessStartLocalTime { get; }
    void Start();
    void Stop();
    bool WaitForStatus(ServiceControllerStatus status, TimeSpan timeout);
}

public interface IShellNotificationSeam
{
    void ShowUnexpectedStop(bool noAnswer);
    int UnexpectedStopCount { get; }
    int NoAnswerStopCount { get; }
    void ResetCounts();
    void ShowBalloon(string title, string text);
    string? LastUnexpectedStopTitle { get; }
    string? LastUnexpectedStopBody { get; }
}

public interface IHttpProbeSeam
{
    bool TryGetOperatorOk(out int statusCode);
}

public sealed class ProductionServiceControllerSeam : IServiceControllerSeam
{
    public bool ServiceExists
    {
        get
        {
            try
            {
                using var sc = new ServiceController(ShellPaths.ServiceName);
                _ = sc.Status;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public ServiceControllerStatus Status
    {
        get
        {
            using var sc = new ServiceController(ShellPaths.ServiceName);
            sc.Refresh();
            return sc.Status;
        }
    }

    public DateTime? ProcessStartLocalTime
    {
        get
        {
            try
            {
                using var sc = new ServiceController(ShellPaths.ServiceName);
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    return null;
                }

                var pid = ShellNative.GetServiceProcessId(sc.ServiceName);
                if (pid <= 0)
                {
                    return null;
                }

                return ShellNative.GetProcessStartTime(pid);
            }
            catch
            {
                return null;
            }
        }
    }

    public void Start()
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        sc.Start();
    }

    public void Stop()
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        sc.Stop();
    }

    public bool WaitForStatus(ServiceControllerStatus status, TimeSpan timeout)
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        try
        {
            sc.WaitForStatus(status, timeout);
            return sc.Status == status;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class ProductionNotificationSeam : IShellNotificationSeam
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
            try
            {
                ShellTrayHost.CurrentNotifyIcon?.ShowBalloonTip(
                    5000,
                    "\u200f" + LastUnexpectedStopTitle,
                    "\u200f" + LastUnexpectedStopBody,
                    System.Windows.Forms.ToolTipIcon.Warning);
            }
            catch
            {
            }

            return;
        }

        UnexpectedStopCount++;
        LastUnexpectedStopTitle = "سرور چک‌پوینت متوقف شد";
        LastUnexpectedStopBody = "از منوی کنار ساعت «شروع سرور» را بزنید.";
        try
        {
            ShellTrayHost.CurrentNotifyIcon?.ShowBalloonTip(
                5000,
                "\u200f" + LastUnexpectedStopTitle,
                "\u200f" + LastUnexpectedStopBody,
                System.Windows.Forms.ToolTipIcon.Warning);
        }
        catch
        {
        }
    }

    public void ResetCounts()
    {
        UnexpectedStopCount = 0;
        NoAnswerStopCount = 0;
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            ShellTrayHost.CurrentNotifyIcon?.ShowBalloonTip(5000, "\u200f" + title, "\u200f" + text, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch
        {
        }
    }
}

public sealed class ProductionHttpProbeSeam : IHttpProbeSeam
{
    public bool TryGetOperatorOk(out int statusCode)
    {
        statusCode = 0;
        try
        {
            var request = System.Net.WebRequest.Create(ShellPaths.PanelUrl);
            request.Timeout = 3000;
            using var response = (System.Net.HttpWebResponse)request.GetResponse();
            statusCode = (int)response.StatusCode;
            return statusCode == 200;
        }
        catch
        {
            return false;
        }
    }
}

public readonly record struct ShellMessageBoxRequest(
    string Text,
    string Caption,
    MessageBoxButtons Buttons,
    MessageBoxIcon Icon,
    MessageBoxDefaultButton DefaultButton);

public static class ShellSeams
{
    public static IServiceControllerSeam Service { get; set; } = new ProductionServiceControllerSeam();
    public static IShellNotificationSeam Notifications { get; set; } = new ProductionNotificationSeam();
    public static IHttpProbeSeam HttpProbe { get; set; } = new ProductionHttpProbeSeam();
    public static Func<int>? ReadOpenSessionCount { get; set; }
    public static Func<LocalServerStatusDto?>? ReadLocalStatus { get; set; }
    public static bool TrayStopInProgress { get; set; }
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    public static Func<TimeSpan> SystemUptime { get; set; } = () => TimeSpan.FromMilliseconds(Environment.TickCount & int.MaxValue);
    public static Func<string, bool>? MaintenanceFlagExists { get; set; }
    public static Func<ShellMessageBoxRequest, DialogResult>? MessageBoxHandler { get; set; }
    public static ShellMessageBoxRequest? LastMessageBoxRequest { get; set; }
    public static Action? BeforeBuildSnapshot { get; set; }
    public static Action? OnTickEntered { get; set; }
    public static Action? OnTickSkippedReentrant { get; set; }
    public static Action<string>? OnTickDiag { get; set; }

    public static DialogResult ShowMessageBox(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
    {
        var request = new ShellMessageBoxRequest(text, caption, buttons, icon, defaultButton);
        LastMessageBoxRequest = request;
        if (MessageBoxHandler is not null)
        {
            return MessageBoxHandler(request);
        }

        return MessageBox.Show(
            text,
            caption,
            buttons,
            icon,
            defaultButton,
            MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
    }
}