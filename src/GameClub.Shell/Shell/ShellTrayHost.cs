using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace GameClub.Shell.Shell;

internal sealed class ShellTrayHost : IDisposable
{
    public static NotifyIcon? CurrentNotifyIcon { get; private set; }

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _statusLine;
    private readonly ToolStripMenuItem _reloadItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly MainWindow _window;
  private bool _trayHintShown;

    public ShellTrayHost(MainWindow window)
    {
        _window = window;
        _trayHintShown = WindowPlacementStore.TrayHintShown;
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "\u200fچک‌پوینت",
            Icon = LoadIcon("tray-stopped.ico"),
        };
        CurrentNotifyIcon = _icon;
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();

        _menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        _statusLine = new ToolStripMenuItem("سرور: …") { Enabled = false };
        _reloadItem = new ToolStripMenuItem("بازخوانی", null, (_, _) => _window.ReloadPanel());
        _startItem = new ToolStripMenuItem("شروع سرور", null, (_, _) => LaunchServerControlForTests("start"));
        _stopItem = new ToolStripMenuItem("توقف سرور", null, (_, _) => LaunchServerControlForTests("stop"));
        _autostartItem = new ToolStripMenuItem("اجرای خودکار پنجره با ویندوز", null, (_, _) => ToggleAutostart())
        {
            CheckOnClick = true,
        };
        _autostartItem.Checked = AutostartRegistry.IsEnabled;

        _menu.Items.Add(_statusLine);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("باز کردن پنل", null, (_, _) => _window.ShowFromTray()) { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(_reloadItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_startItem);
        _menu.Items.Add(_stopItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("خروج", null, (_, _) => _window.ExitShell()));

        _icon.ContextMenuStrip = _menu;
    }

    public void UpdateSnapshot(ServerStateSnapshot snapshot)
    {
        _statusLine.Text = snapshot.MenuStatusLine;
        _icon.Text = TruncateToolTip("\u200f" + snapshot.TooltipLine1);
        _icon.Icon = snapshot.IconState switch
        {
            TrayIconState.Green => LoadIcon("tray-green.ico"),
            TrayIconState.Yellow => LoadIcon("tray-yellow.ico"),
            _ => LoadIcon("tray-red.ico"),
        };

        var running = snapshot.VisualState == ShellServerVisualState.Running;
        _stopItem.Enabled = running || snapshot.MenuStatusLine.Contains("بدون پاسخ");
        _startItem.Enabled = snapshot.VisualState is ShellServerVisualState.Stopped or ShellServerVisualState.Error;
    }

    public void ShowFirstCloseBalloonIfNeeded()
    {
        if (_trayHintShown)
        {
            return;
        }

        _trayHintShown = true;
        _icon.ShowBalloonTip(
            5000,
            "\u200fچک‌پوینت هنوز باز است",
            "\u200fپنل در آیکن چک‌پوینت کنار ساعت ویندوز است. برای باز کردنش روی آیکن بزنید.",
            ToolTipIcon.Info);
    }

    private void ToggleAutostart()
    {
        AutostartRegistry.SetEnabled(_autostartItem.Checked);
    }

    internal static void LaunchServerControlForTests(string verb) => LaunchServerControl(verb);

    private static void LaunchServerControl(string verb)
    {
        var isStop = string.Equals(verb, "stop", StringComparison.OrdinalIgnoreCase);
        if (isStop)
        {
            ShellSeams.SetTrayStopInProgress(true, "ShellTrayHost.LaunchServerControl");
        }

        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Assembly.GetExecutingAssembly().Location;
            var startInfo = new ProcessStartInfo(exe, "--server-control " + verb)
            {
                Verb = "runas",
                UseShellExecute = true,
            };
            var start = ShellSeams.StartElevatedProcess ?? Process.Start;
            var process = start(startInfo);
            if (isStop && process is not null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    try
                    {
                        if (process.ExitCode != 0)
                        {
                            ShellSeams.SetTrayStopInProgress(false, "ShellTrayHost.elevated-stop-exit");
                        }
                    }
                    catch
                    {
                    }
                };
            }
        }
        catch
        {
            ShellSeams.SetTrayStopInProgress(false, "ShellTrayHost.ProcessStartFailed");
        }
    }

    private static string TruncateToolTip(string text)
    {
        if (text.Length <= 63)
        {
            return text;
        }

        return text.Substring(0, 60) + "…";
    }

    internal static Icon LoadTrayIcon(string name) => LoadIcon(name);

    private static Icon LoadIcon(string name)
    {
        const string assemblyName = "GameClub.Shell";
        var uri = new Uri($"pack://application:,,,/{assemblyName};component/Assets/{name}", UriKind.Absolute);
        try
        {
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream is not null)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch
        {
            // fall through to manifest resource
        }

        var manifestName = $"{assemblyName}.Assets.{name.Replace('/', '.')}";
        using (var manifest = Assembly.GetExecutingAssembly().GetManifestResourceStream(manifestName))
        {
            if (manifest is not null)
            {
                return new Icon(manifest);
            }
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        CurrentNotifyIcon = null;
    }
}