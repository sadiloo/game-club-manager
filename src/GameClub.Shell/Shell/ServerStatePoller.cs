using System;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Threading;

namespace GameClub.Shell.Shell;

internal enum ShellServerVisualState
{
    Running,
    Starting,
    Stopping,
    Stopped,
    Error,
    NoAnswer,
}

internal sealed class ServerStateSnapshot
{
    public ShellServerVisualState VisualState { get; set; }
    public TrayIconState IconState { get; set; }
    public string TooltipLine1 { get; set; } = "";
    public string MenuStatusLine { get; set; } = "";
    public bool OperatorAnswers { get; set; }
    public bool IsUnexpectedStopRed { get; set; }
}

internal sealed class ServerStatePoller : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private TrayIconState _lastIcon = TrayIconState.Red;
    private bool _wasServiceRunningClass;
    private bool _armedForUnexpectedToast = true;
    private DateTime _noAnswerSinceUtc = DateTime.MinValue;
    private DateTime _bootUtc = DateTime.UtcNow;

    public event Action<ServerStateSnapshot>? Changed;

    public ServerStatePoller()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    public ServerStateSnapshot Current { get; private set; } = new();

    private void Tick()
    {
        var snapshot = BuildSnapshot();
        if (_armedForUnexpectedToast
            && _wasServiceRunningClass
            && snapshot.IconState == TrayIconState.Red
            && snapshot.IsUnexpectedStopRed
            && !ShellSeams.TrayStopInProgress
            && !MaintenanceFlagPresent())
        {
            ShellSeams.Notifications.ShowUnexpectedStop(snapshot.VisualState == ShellServerVisualState.NoAnswer);
        }

        if (snapshot.IconState == TrayIconState.Green)
        {
            _armedForUnexpectedToast = true;
        }

        _wasServiceRunningClass = snapshot.VisualState is ShellServerVisualState.Running
            or ShellServerVisualState.Starting
            or ShellServerVisualState.Stopping;
        _lastIcon = snapshot.IconState;
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }

    private static bool MaintenanceFlagPresent()
    {
        try
        {
            return File.Exists(Path.Combine(ShellPaths.ProgramDataLogs, "maintenance.flag"));
        }
        catch
        {
            return false;
        }
    }

    private ServerStateSnapshot BuildSnapshot()
    {
        if (!ShellSeams.Service.ServiceExists)
        {
            return Red(ShellServerVisualState.Stopped, "سرور: خاموش", "سرور: خاموش یا بدون پاسخ", unexpected: true);
        }

        ServiceControllerStatus status;
        try
        {
            status = ShellSeams.Service.Status;
        }
        catch
        {
            return Red(ShellServerVisualState.Stopped, "سرور: خاموش", "سرور: خاموش", unexpected: true);
        }

        LocalServerStatusClient.TryRead(out var local);
        var dbOk = local?.DbOk == true;
        var startedUtc = local?.StartedAtUtc;

        if (status is ServiceControllerStatus.StartPending or ServiceControllerStatus.StopPending)
        {
            _noAnswerSinceUtc = DateTime.MinValue;
            var menu = status == ServiceControllerStatus.StartPending
                ? "سرور: در حال روشن شدن…"
                : "سرور: در حال خاموش شدن…";
            return Yellow(ShellServerVisualState.Stopping, menu, "سرور: در حال روشن/خاموش شدن");
        }

        if (status == ServiceControllerStatus.Stopped)
        {
            _noAnswerSinceUtc = DateTime.MinValue;
            if (IsDelayedBootWindow())
            {
                return Yellow(ShellServerVisualState.Starting, "سرور: در حال روشن شدن…", "سرور: در حال روشن/خاموش شدن");
            }

            return Red(ShellServerVisualState.Stopped, "سرور: خاموش", "سرور: خاموش", unexpected: true);
        }

        if (status != ServiceControllerStatus.Running)
        {
            return Yellow(ShellServerVisualState.Starting, "سرور: در حال روشن شدن…", "سرور: در حال روشن/خاموش شدن");
        }

        if (dbOk)
        {
            _noAnswerSinceUtc = DateTime.MinValue;
            var menu = FormatRunningMenu(startedUtc);
            return new ServerStateSnapshot
            {
                VisualState = ShellServerVisualState.Running,
                IconState = TrayIconState.Green,
                TooltipLine1 = "سرور: روشن",
                MenuStatusLine = menu,
                OperatorAnswers = true,
            };
        }

        if (_noAnswerSinceUtc == DateTime.MinValue)
        {
            _noAnswerSinceUtc = DateTime.UtcNow;
        }

        var elapsed = DateTime.UtcNow - _noAnswerSinceUtc;
        if (elapsed < TimeSpan.FromSeconds(90))
        {
            return Yellow(ShellServerVisualState.Starting, "سرور: بدون پاسخ", "سرور: در حال روشن/خاموش شدن", operatorAnswers: false);
        }

        return Red(ShellServerVisualState.NoAnswer, "سرور: بدون پاسخ", "سرور: خاموش یا بدون پاسخ", unexpected: true);
    }

    private bool IsDelayedBootWindow()
    {
        try
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount);
            return uptime < TimeSpan.FromMinutes(5);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatRunningMenu(DateTimeOffset? startedUtc)
    {
        if (startedUtc is null)
        {
            return "سرور: روشن";
        }

        var tehran = TimeZoneInfo.ConvertTime(startedUtc.Value, GetTehranZone());
        var today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, GetTehranZone()).Date;
        var startDate = tehran.Date;
        var hm = ToPersianDigits(tehran.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (startDate == today)
        {
            return $"سرور: روشن — از ساعت {hm}";
        }

        if (startDate == today.AddDays(-1))
        {
            return $"سرور: روشن — از دیروز {hm}";
        }

        var date = ToPersianDigits(tehran.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        return $"سرور: روشن — از {date} {hm}";
    }

    private static ServerStateSnapshot Yellow(ShellServerVisualState visual, string menu, string tooltip, bool operatorAnswers = false)
        => new()
        {
            VisualState = visual,
            IconState = TrayIconState.Yellow,
            TooltipLine1 = tooltip,
            MenuStatusLine = menu,
            OperatorAnswers = operatorAnswers,
        };

    private static ServerStateSnapshot Red(ShellServerVisualState visual, string menu, string tooltip, bool unexpected)
        => new()
        {
            VisualState = visual,
            IconState = TrayIconState.Red,
            TooltipLine1 = tooltip,
            MenuStatusLine = menu,
            IsUnexpectedStopRed = unexpected,
        };

    private static TimeZoneInfo GetTehranZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Iran Standard Time");
        }
        catch
        {
            return TimeZoneInfo.CreateCustomTimeZone("Tehran", TimeSpan.FromHours(3.5), "Tehran", "Tehran");
        }
    }

    internal static string ToPersianDigits(string input)
    {
        var digits = new[] { '۰', '۱', '۲', '۳', '۴', '۵', '۶', '۷', '۸', '۹' };
        var chars = input.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= '0' && chars[i] <= '9')
            {
                chars[i] = digits[chars[i] - '0'];
            }
        }

        return new string(chars);
    }

    public void Dispose() => _timer.Dispose();
}