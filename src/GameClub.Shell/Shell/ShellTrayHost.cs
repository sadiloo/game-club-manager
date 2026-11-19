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
        _startItem = new ToolStripMenuItem("شروع سرور", null, (_, _) => LaunchServerControl("start"));
        _stopItem = new ToolStripMenuItem("توقف سرور", null, (_, _) => LaunchServerControl("stop"));
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

    private static void LaunchServerControl(string verb)
    {
        if (string.Equals(verb, "stop", StringComparison.OrdinalIgnoreCase))
        {
            ShellSeams.TrayStopInProgress = true;
        }

        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo(exe, "--server-control " + verb)
            {
                Verb = "runas",
                UseShellExecute = true,
            });
        }
        catch
        {
            ShellSeams.TrayStopInProgress = false;
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

    private static Icon LoadIcon(string name)
    {
        var uri = new Uri($"pack://application:,,,/Assets/{name}", UriKind.Absolute);
        using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        CurrentNotifyIcon = null;
    }
}