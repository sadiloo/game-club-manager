using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    private bool _pendingOffline;
    private string _pendingOfflineState = "stopped";
    private string? _lastNavigationUri;
    private bool _offlineShown;

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
                if (snap.VisualState == ShellServerVisualState.Running && _webViewReady)
                {
                    if (_offlineShown)
                    {
                        _offlineShown = false;
                        var recoverUri = new Uri(ShellPaths.PanelUrl);
                        LogPanelNavigate("RunningRecover", recoverUri);
                        PanelWebView.Source = recoverUri;
                    }
                    else
                    {
                        _ = NavigatePanelAsync();
                    }
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
                _lastNavigationUri = args.Uri;
                _navigationStartingCount++;
                if (!args.Uri.StartsWith(ShellPaths.PanelOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var blocked))
                    {
                        var host = (blocked.Host ?? "").ToLowerInvariant();
                        var hash8 = Sha256Hex8(host);
                        ShellFileLog.Write($"navigation-blocked scheme={blocked.Scheme} host_hash8={hash8}");
                    }
                    else
                    {
                        ShellFileLog.Write("navigation-blocked scheme=unknown host_hash8=00000000");
                    }
                }
            };

            PanelWebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var blocked))
                {
                    var host = (blocked.Host ?? "").ToLowerInvariant();
                    ShellFileLog.Write($"new-window-blocked scheme={blocked.Scheme} host_hash8={Sha256Hex8(host)}");
                }
                else
                {
                    ShellFileLog.Write("new-window-blocked scheme=unknown host_hash8=00000000");
                }
            };

            PanelWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

            _webViewReady = true;
            PanelWebView.Visibility = Visibility.Visible;
            NativeHost.Visibility = Visibility.Collapsed;

            if (_pendingOffline)
            {
                _pendingOffline = false;
                await PresentOfflinePageAsync(_pendingOfflineState);
            }
            else
            {
                await NavigatePanelAsync();
            }
        }
        catch
        {
            NativeHost.Visibility = Visibility.Visible;
            PanelWebView.Visibility = Visibility.Collapsed;
            NativeTitle.Text = "برای نمایش پنل، WebView2 لازم است";
            NativeBody.Text = "بستهٔ WebView2 روی این رایانه نصب نیست یا خراب است.";
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_offlineShown)
        {
            return;
        }

        if (e.IsSuccess || !IsLoopbackPanelTarget(_lastNavigationUri ?? ""))
        {
            return;
        }

        var state = _poller?.Current.VisualState switch
        {
            ShellServerVisualState.Starting => "starting",
            _ => "stopped",
        };
        _ = PresentOfflinePageAsync(state);
    }

    private static bool IsLoopbackPanelTarget(string uriString)
    {
        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var host = (uri.Host ?? "").ToLowerInvariant();
        return host is "127.0.0.1" or "localhost" or "::1";
    }

    private static string Sha256Hex8(string lowercaseHost)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(lowercaseHost));
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            hex.Append(b.ToString("x2"));
        }

        return hex.Length >= 8 ? hex.ToString(0, 8) : hex.ToString().PadRight(8, '0');
    }

    private static void LogPanelNavigate(string caller, Uri uri)
    {
        var host = (uri.Host ?? "").ToLowerInvariant();
        ShellFileLog.Write($"panel-navigate caller={caller} scheme={uri.Scheme} host_hash8={Sha256Hex8(host)}");
    }

    private void LogNavigateToString(string caller)
    {
        ShellFileLog.Write($"panel-navigate caller={caller} scheme=data host_hash8={Sha256Hex8("offline-html")}");
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

    public Task NavigatePanelAsync()
    {
        if (!_webViewReady)
        {
            return Task.CompletedTask;
        }

        if (_offlineShown)
        {
            return Task.CompletedTask;
        }

        if (_poller?.Current.VisualState != ShellServerVisualState.Running)
        {
            return Task.CompletedTask;
        }

        var uri = new Uri(ShellPaths.PanelUrl);
        LogPanelNavigate("NavigatePanel", uri);
        PanelWebView.Source = uri;
        return Task.CompletedTask;
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
        var state = snap.VisualState switch
        {
            ShellServerVisualState.Starting => "starting",
            _ => "stopped",
        };

        if (!_webViewReady)
        {
            _pendingOffline = true;
            _pendingOfflineState = state;
            return;
        }

        if (DateTime.UtcNow - _lastReconnectAttempt < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastReconnectAttempt = DateTime.UtcNow;
        await PresentOfflinePageAsync(state);
    }

    private Task PresentOfflinePageAsync(string state)
    {
        if (!_webViewReady)
        {
            _pendingOffline = true;
            _pendingOfflineState = state;
            return Task.CompletedTask;
        }

        _offlineShown = true;
        var html = LoadOfflineHtml(state);
        LogNavigateToString("PresentOffline");
        PanelWebView.NavigateToString(html);
        return Task.CompletedTask;
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