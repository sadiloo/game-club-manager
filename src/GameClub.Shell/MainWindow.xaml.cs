using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Reflection;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Controls;
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
    private CoreWebView2Environment? _webViewEnvironment;
    private readonly WebViewProcessRecoveryPolicy _processRecoveryPolicy = new();
    private readonly PanelNavigationRetryPolicy _panelNavRetryPolicy = new();
    private int _panelProbeInFlight;
    private ShellServerVisualState _lastObservedVisualState = ShellServerVisualState.Starting;
    private bool _runningPanelLoadFailing;
    private int _processRecoveryInFlight;
    private int _pendingBrowserRecreate;
    private readonly HashSet<ulong> _blockedNavigationIds = new();

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
            var priorVisual = _lastObservedVisualState;
            _lastObservedVisualState = snap.VisualState;
            if (snap.VisualState == ShellServerVisualState.Running && priorVisual != ShellServerVisualState.Running)
            {
                _panelNavRetryPolicy.Reset();
                _runningPanelLoadFailing = false;
            }

            if (snap.VisualState == ShellServerVisualState.Running && _webViewReady)
            {
                if (_offlineShown && _runningPanelLoadFailing)
                {
                    TryRunningPanelProbeWhenDue();
                }
                else if (_offlineShown)
                {
                    _offlineShown = false;
                    _currentOfflineState = null;
                    _allowOfflineDataNav = false;
                    var recoverUri = new Uri(ShellSeams.ResolvePanelUrl());
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
                    _ = TryProbeThenNavigatePanelAsync();
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
            _webViewEnvironment ??= await CoreWebView2Environment.CreateAsync(userDataFolder: ShellPaths.WebView2UserData);
            _webViewEnvironment.BrowserProcessExited -= OnEnvironmentBrowserProcessExited;
            _webViewEnvironment.BrowserProcessExited += OnEnvironmentBrowserProcessExited;
            await PanelWebView.EnsureCoreWebView2Async(_webViewEnvironment);
            if (PanelWebView.CoreWebView2 is null)
            {
                throw new InvalidOperationException("Panel WebView2 core is null after EnsureCoreWebView2Async");
            }

            WirePanelWebView(PanelWebView.CoreWebView2);

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
                    await TryProbeThenNavigatePanelAsync();
                }
            }
            else
            {
                if (_poller?.Current is { OperatorAnswers: false } snap && ShouldPresentPendingOffline())
                {
                    await ShowOfflineAsync(snap);
                }
                else if (_poller?.Current.VisualState == ShellServerVisualState.Running)
                {
                    await TryProbeThenNavigatePanelAsync();
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

    internal static string HostHash8ForTests(string host) => Sha256Hex8(host.ToLowerInvariant());

    internal ShellServerVisualState? PollerVisualStateForTests => _poller?.Current.VisualState;

    internal bool OfflineDataNavigationPermissionArmedForTests => _allowOfflineDataNav;

    internal void ArmOfflineDataNavigationPermissionForTests()
    {
        _allowOfflineDataNav = true;
    }

    internal void NavigateToStringWithOfflineDataPermissionForTests(string html)
    {
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
        finally
        {
            if (_closed)
            {
                _allowOfflineDataNav = false;
            }
        }
    }

    private void WirePanelWebView(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsStatusBarEnabled = false;

        core.NavigationStarting += OnPanelNavigationStarting;
        core.NewWindowRequested += OnPanelNewWindowRequested;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnWebViewProcessFailed;
    }

    private void OnPanelNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _lastNavigationUri = args.Uri;
        _navigationStartingCount++;
        if (_allowOfflineDataNav
            && args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            _allowOfflineDataNav = false;
            return;
        }

        if (!PanelNavigationOrigin.IsAllowedPanelNavigationUri(args.Uri))
        {
            args.Cancel = true;
            _blockedNavigationIds.Add(args.NavigationId);
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var blocked))
            {
                var host = (blocked.Host ?? "").ToLowerInvariant();
                var hash8 = Sha256Hex8(host);
                ShellFileLog.Write(
                    $"navigation-blocked id={args.NavigationId} scheme={blocked.Scheme} host_hash8={hash8}");
            }
            else
            {
                ShellFileLog.Write($"navigation-blocked id={args.NavigationId} scheme=unknown host_hash8=00000000");
            }
        }
    }

    private static void OnPanelNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
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
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        var lastUri = _lastNavigationUri ?? "";
        var loopbackPanel = IsLoopbackPanelTarget(lastUri);

        if (_blockedNavigationIds.Remove(e.NavigationId))
        {
            LogOfflineTrace(
                "nav-completed-blocked",
                $"id={e.NavigationId} IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} http={e.HttpStatusCode} lastUri={FormatUriForLog(lastUri)}");
            return;
        }

        if (lastUri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
        {
            _allowOfflineDataNav = false;
            LogOfflineTrace(
                "nav-completed-offline-data",
                $"id={e.NavigationId} IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} offlineState={_currentOfflineState}");
            return;
        }

        if (loopbackPanel && PanelNavigationOrigin.IsAllowedPanelNavigationUri(lastUri))
        {
            if (ShouldRecoverFromPanelNavigation(e))
            {
                _panelNavRetryPolicy.Reset();
                _runningPanelLoadFailing = false;
                if (_offlineShown)
                {
                    _offlineShown = false;
                    _currentOfflineState = null;
                }

                LogOfflineTrace(
                    "nav-completed-panel-ok",
                    $"webError={e.WebErrorStatus} http={e.HttpStatusCode} lastUri={FormatUriForLog(lastUri)}");
                return;
            }

            if (_poller?.Current.VisualState == ShellServerVisualState.Running)
            {
                LogOfflineTrace(
                    "nav-completed-panel-fail",
                    $"id={e.NavigationId} IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} http={e.HttpStatusCode} lastUri={FormatUriForLog(lastUri)}");
                HandleRunningPanelNavigationFailure();
                return;
            }
        }

        if (_offlineShown && !_runningPanelLoadFailing)
        {
            LogOfflineTrace(
                "nav-completed-skip",
                "offlineAlreadyShown=true");
            return;
        }

        if (!loopbackPanel)
        {
            LogOfflineTrace(
                "nav-completed-skip",
                $"IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} loopbackTarget=false lastUri={FormatUriForLog(lastUri)}");
            return;
        }

        if (e.IsSuccess)
        {
            LogOfflineTrace(
                "nav-completed-skip",
                $"IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} loopbackTarget={loopbackPanel} lastUri={FormatUriForLog(lastUri)}");
            return;
        }

        var state = _poller?.Current.VisualState switch
        {
            ShellServerVisualState.Starting => "starting",
            _ => "stopped",
        };
        LogOfflineTrace(
            "nav-completed-fallback",
            $"IsSuccess={e.IsSuccess} webError={e.WebErrorStatus} offlineState={state} lastUri={FormatUriForLog(lastUri)}");
        _panelNavigated = false;
        _ = PresentOfflinePageAsync(state);
    }

    private void HandleRunningPanelNavigationFailure()
    {
        _runningPanelLoadFailing = true;
        var utcNow = DateTime.UtcNow;
        _panelNavRetryPolicy.RecordFailure(utcNow);
        var attempt = _panelNavRetryPolicy.FailureCount;
        var nextDelay = _panelNavRetryPolicy.GetNextDelayAfterLastFailure();
        ShellFileLog.Write($"panel-nav-retry attempt={attempt} nextDelaySec={nextDelay.TotalSeconds:F0}");
        _panelNavigated = false;
        if (!_offlineShown)
        {
            _ = PresentOfflinePageAsync("panel-unavailable");
        }
        else
        {
            RestoreRunningPanelOfflineView();
        }
    }

    private void TryRunningPanelProbeWhenDue()
    {
        var utcNow = DateTime.UtcNow;
        if (!_panelNavRetryPolicy.CanAttempt(utcNow))
        {
            LogOfflineTrace(
                "panel-retry-throttle-skip",
                $"failureCount={_panelNavRetryPolicy.FailureCount} nextDelaySec={_panelNavRetryPolicy.GetNextDelayAfterLastFailure().TotalSeconds:F0}");
            return;
        }

        _ = TryRunningPanelProbeCoreAsync();
    }

    private async Task TryRunningPanelProbeCoreAsync()
    {
        if (Interlocked.CompareExchange(ref _panelProbeInFlight, 1, 0) != 0)
        {
            ShellFileLog.Write("panel-nav-retry action=probe outcome=skipped reason=probe-in-flight");
            return;
        }

        try
        {
            var attempt = _panelNavRetryPolicy.FailureCount + 1;
            var nextDelayBeforeAttempt = _panelNavRetryPolicy.GetNextDelayAfterLastFailure();
            var (ok, statusCode) = await Task.Run(ProbePanelHttp);
            if (!ok)
            {
                var utcNow = DateTime.UtcNow;
                _panelNavRetryPolicy.RecordFailure(utcNow);
                var nextDelay = _panelNavRetryPolicy.GetNextDelayAfterLastFailure();
                ShellFileLog.Write(
                    $"panel-nav-retry attempt={attempt} nextDelaySec={nextDelay.TotalSeconds:F0} action=probe outcome=fail status={statusCode}");
                LogOfflineTrace(
                    "panel-probe-fail",
                    $"status={statusCode} scheduledDelaySec={nextDelayBeforeAttempt.TotalSeconds:F0}");
                return;
            }

            ShellFileLog.Write(
                $"panel-nav-retry attempt={attempt} nextDelaySec=0 action=probe outcome=ok status={statusCode}");
            await Dispatcher.InvokeAsync(() => NavigatePanelFromProbe());
        }
        finally
        {
            Interlocked.Exchange(ref _panelProbeInFlight, 0);
        }
    }

    private static (bool Ok, int StatusCode) ProbePanelHttp()
    {
        if (!ShellSeams.HttpProbe.TryGetOperatorOk(out var statusCode))
        {
            return (false, statusCode);
        }

        return (statusCode is >= 200 and < 300, statusCode);
    }

    private async Task TryProbeThenNavigatePanelAsync()
    {
        if (Interlocked.CompareExchange(ref _panelProbeInFlight, 1, 0) != 0)
        {
            ShellFileLog.Write("panel-nav-retry action=probe outcome=skipped reason=probe-in-flight");
            return;
        }

        try
        {
            var (ok, statusCode) = await Task.Run(ProbePanelHttp);
            if (ok)
            {
                NavigatePanelFromProbe();
                return;
            }

            EnterRunningPanelLoadFailingMode(statusCode);
        }
        finally
        {
            Interlocked.Exchange(ref _panelProbeInFlight, 0);
        }
    }

    private void EnterRunningPanelLoadFailingMode(int statusCode)
    {
        _runningPanelLoadFailing = true;
        var utcNow = DateTime.UtcNow;
        _panelNavRetryPolicy.RecordFailure(utcNow);
        var attempt = _panelNavRetryPolicy.FailureCount;
        var nextDelay = _panelNavRetryPolicy.GetNextDelayAfterLastFailure();
        ShellFileLog.Write(
            $"panel-nav-retry attempt={attempt} nextDelaySec={nextDelay.TotalSeconds:F0} action=probe outcome=fail status={statusCode}");
        _panelNavigated = false;
        if (!_offlineShown)
        {
            _ = PresentOfflinePageAsync("panel-unavailable");
        }
    }

    private void NavigatePanelFromProbe()
    {
        if (_closed || !_webViewReady)
        {
            return;
        }

        if (_poller?.Current.VisualState != ShellServerVisualState.Running)
        {
            return;
        }

        var uri = new Uri(ShellSeams.ResolvePanelUrl());
        LogPanelNavigate("NavigatePanel", uri);
        LogRaceNav("NavigatePanel.Source");
        _panelNavigated = true;
        PanelWebView.Source = uri;
    }

    private void RestoreRunningPanelOfflineView()
    {
        if (!_webViewReady || !_offlineShown || !_runningPanelLoadFailing)
        {
            return;
        }

        var uri = CurrentPanelUriForTrace();
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var html = LoadOfflineHtml(_currentOfflineState ?? "panel-unavailable");
        NavigateOfflineHtmlToPanel(html, "RestoreRunningPanelOffline");
    }

    private bool IsActiveOnPanelOrigin()
    {
        var uri = CurrentPanelUriForTrace();
        return PanelNavigationOrigin.IsAllowedPanelNavigationUri(uri);
    }

    private void OnEnvironmentBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        ShellFileLog.Write(
            $"webview-process-failed kind=BrowserProcessExited environmentExitKind={e.BrowserProcessExitKind} pid={e.BrowserProcessId}");
        _webViewReady = false;
        try
        {
            if (PanelWebView.CoreWebView2 is not null)
            {
                ShellFileLog.Write(
                    "webview-process-failed kind=BrowserProcessExited outcome=defer-to-core trigger=environment-browser-exited");
                return;
            }
        }
        catch (InvalidOperationException)
        {
        }

        BeginBrowserProcessRecovery("environment-browser-exited");
    }

    private void BeginBrowserProcessRecovery(string trigger)
    {
        if (_closed)
        {
            return;
        }

        if (Volatile.Read(ref _processRecoveryInFlight) == 1)
        {
            Volatile.Write(ref _pendingBrowserRecreate, 1);
            ShellFileLog.Write(
                $"webview-process-failed kind=BrowserProcessExited outcome=escalate-recreate reason=recovery-in-flight trigger={trigger}");
            return;
        }

        if (Interlocked.CompareExchange(ref _processRecoveryInFlight, 1, 0) != 0)
        {
            Volatile.Write(ref _pendingBrowserRecreate, 1);
            ShellFileLog.Write(
                $"webview-process-failed kind=BrowserProcessExited outcome=escalate-recreate reason=recovery-in-flight trigger={trigger}");
            return;
        }

        _ = RunBrowserProcessRecoveryChainAsync(trigger);
    }

    private async Task RunBrowserProcessRecoveryChainAsync(string trigger)
    {
        try
        {
            await RunProcessFailureRecoveryAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);
        }
        catch (Exception ex)
        {
            ShellFileLog.Write(
                $"webview-process-failed kind=BrowserProcessExited outcome=error trigger={trigger} detail={ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _processRecoveryInFlight, 0);
            if (Volatile.Read(ref _pendingBrowserRecreate) == 1)
            {
                Volatile.Write(ref _pendingBrowserRecreate, 0);
                if (Interlocked.CompareExchange(ref _processRecoveryInFlight, 1, 0) == 0)
                {
                    try
                    {
                        await RunProcessFailureRecoveryAsync(CoreWebView2ProcessFailedKind.BrowserProcessExited);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _processRecoveryInFlight, 0);
                    }
                }
            }
        }
    }

    private async void OnWebViewProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        CoreWebView2? core = sender as CoreWebView2;
        if (core is null)
        {
            try
            {
                core = PanelWebView.CoreWebView2;
            }
            catch (InvalidOperationException)
            {
                core = null;
            }
        }

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            _webViewReady = false;
            if (!ShouldRecoverFromProcessFailure(e, core))
            {
                ShellFileLog.Write($"webview-process-failed kind={e.ProcessFailedKind} outcome=log-only");
                return;
            }

            BeginBrowserProcessRecovery("core-process-failed");
            return;
        }

        if (!ShouldRecoverFromProcessFailure(e, core))
        {
            ShellFileLog.Write($"webview-process-failed kind={e.ProcessFailedKind} outcome=log-only");
            return;
        }

        if (Interlocked.CompareExchange(ref _processRecoveryInFlight, 1, 0) != 0)
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={e.ProcessFailedKind} outcome=deferred reason=recovery-in-flight");
            return;
        }

        try
        {
            await RunProcessFailureRecoveryAsync(e.ProcessFailedKind);
        }
        catch (Exception ex)
        {
            ShellFileLog.Write($"webview-process-failed kind={e.ProcessFailedKind} outcome=error detail={ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _processRecoveryInFlight, 0);
        }
    }

    private async Task RunProcessFailureRecoveryAsync(CoreWebView2ProcessFailedKind kind)
    {
        var utcNow = DateTime.UtcNow;
        if (!_processRecoveryPolicy.CanRetry(utcNow))
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={kind} attempt=exhausted outcome=offline-page");
            await Dispatcher.InvokeAsync(new Action(ShowOfflineAfterProcessRecoveryExhausted));
            return;
        }

        var attempt = _processRecoveryPolicy.RecordAttemptAndGetIndex(utcNow);
        var delay = _processRecoveryPolicy.GetBackoffForAttempt(attempt);
        if (delay is null)
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={kind} attempt={attempt} outcome=offline-page reason=no-backoff");
            await Dispatcher.InvokeAsync(new Action(ShowOfflineAfterProcessRecoveryExhausted));
            return;
        }

        ShellFileLog.Write(
            $"webview-process-failed kind={kind} attempt={attempt} delaySec={delay.Value.TotalSeconds:F0} outcome=pending");

        await Task.Delay(delay.Value);
        if (_closed)
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={kind} attempt={attempt} outcome=cancelled reason=window-closed");
            return;
        }

        if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={kind} attempt={attempt} outcome=recreate-browser");
            try
            {
                if (Dispatcher.CheckAccess())
                {
                    await RecreatePanelWebViewAsync();
                }
                else
                {
                    await (await Dispatcher.InvokeAsync(() => RecreatePanelWebViewAsync()));
                }

                ShellFileLog.Write(
                    $"webview-process-failed kind={kind} attempt={attempt} outcome=recreate-done result=success");
            }
            catch (Exception ex)
            {
                ShellFileLog.Write(
                    $"webview-process-failed kind={kind} attempt={attempt} outcome=recreate-done result=failure detail={ex.GetType().Name}");
            }
        }
        else
        {
            ShellFileLog.Write(
                $"webview-process-failed kind={kind} attempt={attempt} outcome=reload");
            await Dispatcher.InvokeAsync(() =>
            {
                if (!_closed && PanelWebView.CoreWebView2 is not null)
                {
                    PanelWebView.CoreWebView2.Reload();
                }
            });
        }
    }

    private static bool ShouldRecoverFromProcessFailure(CoreWebView2ProcessFailedEventArgs e, CoreWebView2? core)
    {
        return e.ProcessFailedKind switch
        {
            CoreWebView2ProcessFailedKind.RenderProcessExited => true,
            CoreWebView2ProcessFailedKind.RenderProcessUnresponsive => true,
            CoreWebView2ProcessFailedKind.BrowserProcessExited => true,
            CoreWebView2ProcessFailedKind.FrameRenderProcessExited => IsMainFrameRenderProcessFailure(e, core),
            _ => false,
        };
    }

    private static bool IsMainFrameRenderProcessFailure(CoreWebView2ProcessFailedEventArgs e, CoreWebView2? core)
    {
        if (core is null)
        {
            return false;
        }

        var mainSource = core.Source ?? "";
        foreach (var frame in e.FrameInfosForFailedProcess)
        {
            if (string.Equals(frame.Source, mainSource, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void ShowOfflineAfterProcessRecoveryExhausted()
    {
        if (_closed)
        {
            return;
        }

        var state = _poller?.Current.VisualState == ShellServerVisualState.Starting ? "starting" : "stopped";
        _panelNavigated = false;
        _ = PresentOfflinePageAsync(state);
    }

    private void UnwirePanelWebView(CoreWebView2? core)
    {
        if (core is null)
        {
            return;
        }

        core.ProcessFailed -= OnWebViewProcessFailed;
        core.NavigationStarting -= OnPanelNavigationStarting;
        core.NewWindowRequested -= OnPanelNewWindowRequested;
        core.NavigationCompleted -= OnNavigationCompleted;
    }

    private async Task RecreatePanelWebViewAsync()
    {
        if (_closed)
        {
            return;
        }

        _webViewReady = false;
        _ensureWebViewTask = null;

        var oldWebView = PanelWebView;
        var zoom = oldWebView.ZoomFactor;
        var visibility = oldWebView.Visibility;
        CoreWebView2? oldCore = null;
        try
        {
            oldCore = oldWebView.CoreWebView2;
        }
        catch (InvalidOperationException)
        {
        }

        UnwirePanelWebView(oldCore);

        try
        {
            UnregisterName("PanelWebView");
        }
        catch
        {
        }

        PanelHost.Children.Remove(oldWebView);
        oldWebView.Dispose();

        var replacement = new WebView2
        {
            Visibility = visibility,
            ZoomFactor = zoom,
        };
        PanelWebView = replacement;
        RegisterName("PanelWebView", replacement);
        PanelHost.Children.Add(replacement);

        if (_webViewEnvironment is not null)
        {
            _webViewEnvironment.BrowserProcessExited -= OnEnvironmentBrowserProcessExited;
            _webViewEnvironment = null;
        }

        _webViewEnvironment = await CoreWebView2Environment.CreateAsync(userDataFolder: ShellPaths.WebView2UserData);
        _webViewEnvironment.BrowserProcessExited += OnEnvironmentBrowserProcessExited;
        await PanelWebView.EnsureCoreWebView2Async(_webViewEnvironment);
        if (PanelWebView.CoreWebView2 is null)
        {
            ShellFileLog.Write("webview-recreate failed reason=null-core");
            return;
        }

        WirePanelWebView(PanelWebView.CoreWebView2);
        _webViewReady = true;
        _panelNavigated = false;
        PanelWebView.Visibility = Visibility.Visible;
        NativeHost.Visibility = Visibility.Collapsed;

        if (_offlineShown)
        {
            var state = _currentOfflineState ?? "stopped";
            await PresentOfflinePageAsync(state);
        }
        else if (_poller?.Current.VisualState == ShellServerVisualState.Running)
        {
            await NavigatePanelAsync();
        }
        else if (_poller?.Current is { } snap && !snap.OperatorAnswers)
        {
            await ShowOfflineAsync(snap);
        }
    }

    private bool ShouldRecoverFromPanelNavigation(CoreWebView2NavigationCompletedEventArgs e) =>
        PanelNavigationHttpOutcome.ShouldRecoverFromPanelNavigation(
            _runningPanelLoadFailing,
            e.IsSuccess,
            e.WebErrorStatus == CoreWebView2WebErrorStatus.Unknown,
            e.HttpStatusCode);

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

    private void NavigateOfflineHtmlToPanel(string html, string caller)
    {
        var dataUri = "data:text/html;charset=utf-8," + Uri.EscapeDataString(html);
        _lastNavigationUri = dataUri;
        LogNavigateToString(caller);
        LogRaceNav($"{caller}.NavigateData");
        _allowOfflineDataNav = true;
        try
        {
            PanelWebView.CoreWebView2!.Navigate(dataUri);
        }
        catch
        {
            _allowOfflineDataNav = false;
            throw;
        }
        finally
        {
            if (_closed)
            {
                _allowOfflineDataNav = false;
            }
        }
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

        if (!_runningPanelLoadFailing && _panelNavigated && IsActiveOnPanelOrigin())
        {
            return Task.CompletedTask;
        }

        if (_runningPanelLoadFailing)
        {
            return Task.CompletedTask;
        }

        return TryProbeThenNavigatePanelAsync();
    }

    public void ReloadPanel()
    {
        if (!_webViewReady)
        {
            return;
        }

        try
        {
            LogRaceNav("ReloadPanel");
            PanelWebView.CoreWebView2?.Reload();
        }
        catch (InvalidOperationException)
        {
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
        NavigateOfflineHtmlToPanel(html, "PresentOffline");

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
        html = html.Replace("state=stopped", "state=" + state);
        if (string.Equals(state, "panel-unavailable", StringComparison.Ordinal))
        {
            html = html.Replace(
                "سرور در دسترس نیست؛ دوباره تلاش می‌شود…",
                "سرور روشن است؛ پنل در دسترس نیست");
            html = html.Replace(
                "برای روشن کردن سرور، روی آیکن چک‌پوینت کنار ساعت ویندوز راست‌کلیک کنید و «شروع سرور» را بزنید.",
                "چند لحظه صبر کنید. پنل دوباره بارگذاری می‌شود.");
        }

        return html;
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
        _allowOfflineDataNav = false;
        if (_webViewEnvironment is not null)
        {
            _webViewEnvironment.BrowserProcessExited -= OnEnvironmentBrowserProcessExited;
        }

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