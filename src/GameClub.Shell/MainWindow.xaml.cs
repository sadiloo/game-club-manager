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
    private string? _currentOfflineState;
    private bool _allowOfflineDataNav;
    private bool _panelNavigated;
    private bool _closed;
    private Task? _ensureWebViewTask;
    private Action<ServerStateSnapshot>? _onPollerChanged;

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
        _onPollerChanged = OnPollerSnapshotChanged;
        _poller.Changed += _onPollerChanged;

        _flags = new FlagFileWatcher();
        _flags.FlagBalloon += (title, text) => ShellSeams.Notifications.ShowBalloon(title, text);

        AutostartRegistry.EnsureDefaultOn();
        await EnsureWebViewAsync();
    }

    private void OnPollerSnapshotChanged(ServerStateSnapshot snap)
    {
        Dispatcher.Invoke(() =>
        {
            if (_closed)
            {
                return;
            }

            _tray?.UpdateSnapshot(snap);
            if (snap.VisualState == ShellServerVisualState.Running && _webViewReady)
            {
                if (_offlineShown)
                {
                    _offlineShown = false;
                    _currentOfflineState = null;
                    _allowOfflineDataNav = false;
                    var recoverUri = new Uri(ShellPaths.PanelUrl);
                    LogPanelNavigate("RunningRecover", recoverUri);
                    LogRaceNav("RunningRecover.Source");
                    if (_closed)
                    {
                        return;
                    }

                    _panelNavigated = true;
                    PanelWebView.Source = recoverUri;
                }
                else
                {
                    _ = NavigatePanelAsync();
                }
            }
            else if (!snap.OperatorAnswers)
            {
                LogOfflineTrace(
                    "poller-show-offline",
                    $"operatorAnswers=false visual={snap.VisualState} icon={snap.IconState}");
                _ = ShowOfflineAsync(snap);
            }
        });
    }

    private Task EnsureWebViewAsync() => _ensureWebViewTask ??= EnsureWebViewCoreAsync();

    private async Task EnsureWebViewCoreAsync()
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
                if (_allowOfflineDataNav
                    && args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                {
                    _allowOfflineDataNav = false;
                    return;
                }

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
                if (ShouldPresentPendingOffline())
                {
                    await PresentOfflinePageAsync(_pendingOfflineState);
                }
                else if (_poller?.Current.VisualState == ShellServerVisualState.Running)
                {
                    await NavigatePanelAsync();
                }
            }
            else
            {
                if (_poller?.Current is { OperatorAnswers: false } snap && ShouldPresentPendingOffline())
                {
                    await ShowOfflineAsync(snap);
                }
                else
                {
                    await NavigatePanelAsync();
                }
            }
        }
        catch (Exception ex)
        {
            LogOfflineTrace("ensure-webview-failed", ex.GetType().Name);
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
            LogOfflineTrace(
                "nav-completed-skip",
                "offlineAlreadyShown=true");
            return;
        }

        if (e.IsSuccess || !IsLoopbackPanelTarget(_lastNavigationUri ?? ""))
        {
            LogOfflineTrace(
                "nav-completed-skip",
                $"IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} loopbackTarget={IsLoopbackPanelTarget(_lastNavigationUri ?? "")} lastUri={FormatUriForLog(_lastNavigationUri)}");
            return;
        }

        var state = _poller?.Current.VisualState switch
        {
            ShellServerVisualState.Starting => "starting",
            _ => "stopped",
        };
        LogOfflineTrace(
            "nav-completed-fallback",
            $"IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} offlineState={state} lastUri={FormatUriForLog(_lastNavigationUri)}");
        _panelNavigated = false;
        _ = PresentOfflinePageAsync(state);
    }

    private bool IsActiveOnPanelOrigin()
    {
        var uri = CurrentPanelUriForTrace();
        return uri.StartsWith(ShellPaths.PanelOrigin, StringComparison.OrdinalIgnoreCase);
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

    private static string FormatUriForLog(string? uriString)
    {
        var raw = uriString ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return "scheme=data host_hash8=" + Sha256Hex8("offline-html");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return "scheme=unknown host_hash8=00000000";
        }

        var host = (uri.Host ?? "").ToLowerInvariant();
        if (host is "127.0.0.1" or "localhost" or "::1")
        {
            return host + uri.AbsolutePath;
        }

        return $"scheme={uri.Scheme} host_hash8={Sha256Hex8(host)}";
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

    private string CurrentPanelUriForTrace()
    {
        try
        {
            return PanelWebView?.Source?.ToString() ?? _lastNavigationUri ?? "";
        }
        catch
        {
            return _lastNavigationUri ?? "";
        }
    }

    private void LogOfflineTrace(string branch, string reason)
    {
        ShellFileLog.Write(
            $"SHELL-OFFLINE-TRACE UTC={DateTime.UtcNow:O} thread={Environment.CurrentManagedThreadId} branch={branch} reason={reason} uri={FormatUriForLog(CurrentPanelUriForTrace())}");
    }

    private void LogRaceNav(string caller)
    {
        ShellFileLog.Write(
            $"SHELL-RACE-NAV UTC={DateTime.UtcNow:O} thread={Environment.CurrentManagedThreadId} closed={_closed} caller={caller} stack={Environment.StackTrace}");
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
        if (_closed)
        {
            return Task.CompletedTask;
        }

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

        if (_panelNavigated && IsActiveOnPanelOrigin())
        {
            return Task.CompletedTask;
        }

        var uri = new Uri(ShellPaths.PanelUrl);
        LogPanelNavigate("NavigatePanel", uri);
        LogRaceNav("NavigatePanel.Source");
        _panelNavigated = true;
        PanelWebView.Source = uri;
        return Task.CompletedTask;
    }

    public void ReloadPanel()
    {
        if (_webViewReady)
        {
            LogRaceNav("ReloadPanel");
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
            LogOfflineTrace("show-offline-deferred", $"webViewReady=false offlineState={state}");
            _pendingOffline = true;
            _pendingOfflineState = state;
            _ = EnsureWebViewAsync();
            return;
        }

        var sinceLastAttempt = DateTime.UtcNow - _lastReconnectAttempt;
        if (sinceLastAttempt < TimeSpan.FromSeconds(5))
        {
            LogOfflineTrace(
                "show-offline-throttle-skip",
                $"elapsedSec={sinceLastAttempt.TotalSeconds:F3} offlineState={state}");
            return;
        }

        LogOfflineTrace("show-offline-enter", $"offlineState={state}");
        _lastReconnectAttempt = DateTime.UtcNow;
        await PresentOfflinePageAsync(state);
    }

    private Task PresentOfflinePageAsync(string state)
    {
        if (!_webViewReady)
        {
            LogOfflineTrace("present-offline-skip", $"webViewReady=false offlineState={state}");
            _pendingOffline = true;
            _pendingOfflineState = state;
            return Task.CompletedTask;
        }

        if (_offlineShown && string.Equals(_currentOfflineState, state, StringComparison.Ordinal))
        {
            LogOfflineTrace("present-offline-skip", $"offlineState={state} alreadyShown=true");
            return Task.CompletedTask;
        }

        LogOfflineTrace("present-offline-enter", $"offlineState={state}");
        _panelNavigated = false;
        _offlineShown = true;
        _currentOfflineState = state;
        var html = LoadOfflineHtml(state);
        LogNavigateToString("PresentOffline");
        LogRaceNav("PresentOffline.NavigateToString");
        _allowOfflineDataNav = true;
        try
        {
            PanelWebView.NavigateToString(html);
        }
        catch
        {
            _allowOfflineDataNav = false;
            throw;
        }

        LogOfflineTrace("present-offline-navigated", $"offlineState={state}");
        return Task.CompletedTask;
    }

    private bool ShouldPresentPendingOffline()
    {
        var visual = _poller?.Current.VisualState;
        return visual is ShellServerVisualState.Stopped or ShellServerVisualState.NoAnswer;
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
        _closed = true;
        if (_poller is not null && _onPollerChanged is not null)
        {
            _poller.Changed -= _onPollerChanged;
        }

        _poller?.Dispose();
        _flags?.Dispose();
        _tray?.Dispose();
        base.OnClosed(e);
    }
}