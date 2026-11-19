using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using GameClub.Shell.Shell;

namespace GameClub.Shell;

public partial class MainWindow : Window
{
    private ShellTrayHost? _tray;
    private ServerStatePoller? _poller;
    private FlagFileWatcher? _flags;
    private bool _trayHintShown;
    private int _navigationStartingCount;
    private bool _webViewReady;
    private DateTime _lastReconnectAttempt = DateTime.MinValue;

    public int NavigationStartingCount => _navigationStartingCount;
    public double CurrentZoomFactor => PanelWebView?.ZoomFactor ?? 1.0;

    public MainWindow()
    {
        InitializeComponent();
        WindowPlacementStore.Apply(this);
        _trayHintShown = WindowPlacementStore.TrayHintShown;
        Loaded += OnLoadedAsync;
        Closing += OnClosing;
        PreviewKeyDown += OnShellPreviewKeyDown;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        _tray = new ShellTrayHost(this);
        _poller = new ServerStatePoller();
        _poller.Changed += snap =>
        {
            Dispatcher.Invoke(() =>
            {
                _tray?.UpdateSnapshot(snap);
                if (snap.OperatorAnswers && _webViewReady)
                {
                    _ = NavigatePanelAsync();
                }
                else if (!snap.OperatorAnswers)
                {
                    _ = ShowOfflineAsync(snap);
                }
            });
        };

        _flags = new FlagFileWatcher();
        _flags.FlagBalloon += (title, text) => ShellSeams.Notifications.ShowBalloon(title, text);

        AutostartRegistry.EnsureDefaultOn();
        await EnsureWebViewAsync();
    }

    private async Task EnsureWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(ShellPaths.WebView2UserData);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: ShellPaths.WebView2UserData);
            await PanelWebView.EnsureCoreWebView2Async(env);
            var settings = PanelWebView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = true;
            settings.IsStatusBarEnabled = false;

            PanelWebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                _navigationStartingCount++;
                if (!args.Uri.StartsWith(ShellPaths.PanelOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    ShellFileLog.Write($"navigation-blocked url={args.Uri}");
                }
            };

            PanelWebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                ShellFileLog.Write($"new-window-blocked url={args.Uri}");
            };

            _webViewReady = true;
            PanelWebView.Visibility = Visibility.Visible;
            NativeHost.Visibility = Visibility.Collapsed;
            await NavigatePanelAsync();
        }
        catch
        {
            NativeHost.Visibility = Visibility.Visible;
            PanelWebView.Visibility = Visibility.Collapsed;
            NativeTitle.Text = "برای نمایش پنل، WebView2 لازم است";
            NativeBody.Text = "بستهٔ WebView2 روی این رایانه نصب نیست یا خراب است.";
        }
    }

    private void OnShellPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_webViewReady || !PanelWebView.IsFocused)
        {
            return;
        }

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add))
        {
            e.Handled = true;
            PanelWebView.ZoomFactor = Math.Min(5.0, Math.Round(PanelWebView.ZoomFactor + 0.1, 2));
            return;
        }

        if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
        {
            e.Handled = true;
            PanelWebView.ZoomFactor = Math.Max(0.25, Math.Round(PanelWebView.ZoomFactor - 0.1, 2));
            return;
        }

        if (ctrl && e.Key == Key.D0)
        {
            e.Handled = true;
            PanelWebView.ZoomFactor = 1.0;
            return;
        }

        if (ctrl && e.Key == Key.P)
        {
            e.Handled = true;
            return;
        }

        if (e.Key is Key.F5 or Key.F12 || (ctrl && e.Key is Key.R))
        {
            e.Handled = true;
        }
    }

    public async Task NavigatePanelAsync()
    {
        if (!_webViewReady)
        {
            return;
        }

        PanelWebView.Source = new Uri(ShellPaths.PanelUrl);
    }

    public void ReloadPanel()
    {
        if (_webViewReady)
        {
            PanelWebView.CoreWebView2?.Reload();
        }
    }

    private async Task ShowOfflineAsync(ServerStateSnapshot snap)
    {
        if (!_webViewReady)
        {
            return;
        }

        if (DateTime.UtcNow - _lastReconnectAttempt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastReconnectAttempt = DateTime.UtcNow;
        var state = snap.VisualState switch
        {
            ShellServerVisualState.Starting => "starting",
            _ => "stopped",
        };
        var html = LoadOfflineHtml(state);
        PanelWebView.NavigateToString(html);
        await Task.Delay(5000);
        if (_poller?.Current.OperatorAnswers == true)
        {
            await NavigatePanelAsync();
        }
    }

    private static string LoadOfflineHtml(string state)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameClub.Shell.Assets.offline.html")
            ?? throw new InvalidOperationException("offline.html missing");
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd();
        return html.Replace("state=stopped", "state=" + state);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitShell()
    {
        SavePlacement();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (ShellEnvironment.ForceExit)
        {
            SavePlacement();
            return;
        }

        e.Cancel = true;
        Hide();
        _tray?.ShowFirstCloseBalloonIfNeeded();
        SavePlacement();
    }

    private void SavePlacement()
    {
        WindowPlacementStore.Save(this, _trayHintShown);
    }

    protected override void OnClosed(EventArgs e)
    {
        _poller?.Dispose();
        _flags?.Dispose();
        _tray?.Dispose();
        base.OnClosed(e);
    }
}