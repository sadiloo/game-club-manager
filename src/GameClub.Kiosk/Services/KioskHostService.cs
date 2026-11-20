using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameClub.Kiosk.Core.Admin;
using GameClub.Kiosk.Core.Login;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Services;

public sealed class KioskHostService : IAsyncDisposable
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    private readonly Window _shell;
    private readonly TextBlock _status;
    private readonly TextBlock? _loginError;
    private readonly TextBlock _balance;
    private readonly TextBlock _elapsed;
    private readonly TextBlock _freeBand;
    private readonly UIElement _lockPanel;
    private readonly UIElement _launcherPanel;
    private readonly UIElement _playingPanel;
    private readonly ListBox? _gameList;
    private readonly TextBox? _gameSearch;
    private readonly ComboBox? _gameCategory;
    private readonly HttpClient _http = new();
    private readonly KioskRuntime _runtime;
    private readonly KioskShellOrchestrator _orchestrator = new();
    private readonly DispatcherTimer _uiTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _replayTick = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<GameCatalogItem> _catalogCache = [];
    private readonly List<GameCatalogItem> _visibleGames = [];
    private SqliteCatalogCache? _catalogStore;
    private long _catalogVersion;
    private int _credentialsVersion;
    private string _serverBase = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000";
    private string _leaseVerifyPublicKeyPem = "";
    private Guid _deviceId;
    private string _deviceKey = "";
    private string _loadedDeviceKey = "";
    private string _stationId = "";
    private Guid _sessionId;
    private Guid _locallyLockedSessionId;
    private bool _localLockLeaseExpired;
    private bool _logoutInProgress;
    private KioskDisplayDto? _lastDisplay;
    private bool _runCatalogOnFirstConnected;
    private bool _pairingJustFailed;
    private DateTime? _lastHeartbeatAckUtc;
    private Guid _operatorFreeUseId;
    private string _operatorFreeUseMessage = "";
    private readonly KioskFreeUseIdleMonitor _freeUseIdleMonitor = new(DateTime.UtcNow);
    private KioskFreeUseLocalState _freeUseLocal = KioskFreeUseLocalState.Load();
    private DateTime _lastAlivePersistUtc = DateTime.MinValue;
    private KioskAdminRole _lastAdminRole = KioskAdminRole.Operator;
    private readonly KioskHostTestSeam? _testSeam;

    public string ServerBase => _serverBase;

    public KioskHostService(
        Window shell,
        TextBlock status,
        TextBlock? loginError,
        TextBlock balance,
        TextBlock elapsed,
        TextBlock freeBand,
        UIElement lockPanel,
        UIElement launcherPanel,
        UIElement playingPanel,
        ListBox? gameList = null,
        TextBox? gameSearch = null,
        ComboBox? gameCategory = null,
        KioskHostTestSeam? testSeam = null)
    {
        _testSeam = testSeam;
        _shell = shell;
        _status = status;
        _loginError = loginError;
        _balance = balance;
        _elapsed = elapsed;
        _freeBand = freeBand;
        _lockPanel = lockPanel;
        _launcherPanel = launcherPanel;
        _playingPanel = playingPanel;
        _gameList = gameList;
        _gameSearch = gameSearch;
        _gameCategory = gameCategory;
        _runtime = new KioskRuntime(() => CreateHttpClient());
        _runtime.HeartbeatAcknowledged += OnHeartbeatAcknowledged;
        _runtime.HeartbeatTransportFailed += OnHeartbeatTransportFailed;
        _runtime.ConnectionStatusChanged += OnRuntimeConnectionStatus;
        _http.DefaultRequestHeaders.Add("User-Agent", "CheckpointKiosk/1.0");
        _uiTick.Tick += (_, _) => OnUiTick();
        _replayTick.Tick += async (_, _) => await DrainOfflineQueueAsync();
        if (_gameSearch is not null)
        {
            _gameSearch.TextChanged += (_, _) => RenderGameList();
        }

        if (_gameCategory is not null)
        {
            _gameCategory.SelectionChanged += (_, _) => RenderGameList();
        }

        LoadDeviceState();
        TryApplySetupServerBase();
        ShowLockLayout();
        _ = FinalizeInterruptedFreeUseOnBootAsync();
        if (!string.IsNullOrEmpty(_leaseVerifyPublicKeyPem))
        {
            _orchestrator.ConfigureLeaseVerifier(_leaseVerifyPublicKeyPem);
        }

        EnsureStatusUiTickRunning();
        _ = RefreshAsync();
    }

    public HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "CheckpointKiosk/1.0");
        ApplyDeviceHeaders(http);
        return http;
    }

    public void SetServerBase(string url) => _serverBase = url.TrimEnd('/');

    public bool TryUnlockServerUrlEdit(string password)
    {
        var configured = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return false;
        }

        return _orchestrator.TryUnlockAdmin(configured, password);
    }

    public bool TrySafeTestExit(string password)
    {
        var configured = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD");
        return _orchestrator.TrySafeTestExit(configured, password);
    }

    public async Task SubmitPairingCodeAsync(string code)
    {
        var pair = await _http.PostAsync(
            $"{_serverBase}/api/kiosk/pair",
            new StringContent(JsonSerializer.Serialize(new { code }), Encoding.UTF8, "application/json"));
        if (!pair.IsSuccessStatusCode)
        {
            _pairingJustFailed = true;
            RefreshStationStatusText();
            return;
        }

        _pairingJustFailed = false;

        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        _deviceId = doc.RootElement.GetProperty("deviceId").GetGuid();
        var newKey = doc.RootElement.GetProperty("deviceKey").GetString()!;
        OnDeviceCredentialsChanged(newKey);
        _deviceKey = newKey;
        _stationId = doc.RootElement.GetProperty("stationId").GetString()!;
        if (doc.RootElement.TryGetProperty("leaseVerifyPublicKeyPem", out var pem))
        {
            _leaseVerifyPublicKeyPem = pem.GetString() ?? "";
            _orchestrator.ConfigureLeaseVerifier(_leaseVerifyPublicKeyPem);
        }

        SaveDeviceState();
        await EnsureRuntimeAsync();
        await ApplyStationProfileAsync();
        await RefreshCatalogAsync();
        _status.Text = $"Paired to {_stationId}";
        EnsureStatusUiTickRunning();
    }

    public async Task LoginAsync(string loginName, string password)
    {
        var precheck = KioskCustomerLoginGate.Precheck(_deviceId, loginName, password);
        if (precheck != KioskCustomerLoginPrecheck.Ok)
        {
            ShowCustomerLoginError(KioskCustomerLoginGate.MessageFor(precheck));
            return;
        }

        _testSeam?.ResetLoginMetrics();
        var payload = JsonSerializer.Serialize(new { loginName, password, stationId = _stationId });
        var response = await _http.PostAsync(
            $"{_serverBase}/api/kiosk/auth/login",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        _testSeam?.RecordLoginPostBody(payload);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            string? code = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("code", out var c))
                {
                    code = c.GetString();
                }
            }
            catch
            {
                // ignore parse errors
            }

            ShowCustomerLoginError(code switch
            {
                null or "" when response.StatusCode == System.Net.HttpStatusCode.RequestTimeout
                    => KioskLoginStrings.Timeout,
                null or "" when (int)response.StatusCode >= 500 || (int)response.StatusCode == 0
                    => KioskLoginStrings.Unreachable,
                _ => KioskLoginStrings.ForServerCode(code),
            });
            return;
        }

        ClearCustomerLoginError();

        var loginDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _sessionId = loginDoc.RootElement.GetProperty("sessionId").GetGuid();
        _locallyLockedSessionId = Guid.Empty;
        _runtime.BindSession(_sessionId);
        await ApplyStationProfileAsync();
        ShowPlayingLayout();
        _uiTick.Start();
        _replayTick.Start();
        _status.Text = $"Playing on {_stationId}";
        await RefreshCatalogAsync();
    }

    public void LaunchGameAt(int index)
    {
        if (index < 0 || index >= _visibleGames.Count)
        {
            return;
        }

        var game = _visibleGames[index];
        try
        {
            var target = game.ExecutableOrUri.Trim();
            var psi = target.Contains("://", StringComparison.Ordinal)
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(target)
                {
                    Arguments = game.Arguments ?? "",
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(target) ?? Environment.CurrentDirectory,
                };
            var proc = Process.Start(psi);
            if (proc is not null)
            {
                _orchestrator.TrackLaunchedProcess(proc.Id);
            }

            var allow = _orchestrator.FilterVisibleGames(_catalogCache)
                .SelectMany(g => g.AllowedProcessNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (allow.Count > 0)
            {
                _orchestrator.EnforceAllowlist(allow);
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Launch failed: {ex.Message}";
        }
    }

    public async Task LogoutAsync()
    {
        var sessionForLogout = _sessionId;
        ShowLockLayout();
        _replayTick.Stop();
        EnsureStatusUiTickRunning();

        var logoutHttpSucceeded = sessionForLogout == Guid.Empty || _testSeam?.SkipLogoutHttp == true;
        if (sessionForLogout != Guid.Empty)
        {
            _locallyLockedSessionId = sessionForLogout;
            _localLockLeaseExpired = false;
            RefreshStationStatusText();
        }

        if (sessionForLogout != Guid.Empty && _testSeam?.SkipLogoutHttp != true)
        {
            _logoutInProgress = true;
            try
            {
                if (_testSeam?.LogoutHttpDelay is TimeSpan logoutDelay)
                {
                    await Task.Delay(logoutDelay);
                }

                if (_testSeam?.SimulateSuccessfulLogoutHttp == true)
                {
                    logoutHttpSucceeded = true;
                }
                else if (_testSeam?.LogoutHttpThrows == true)
                {
                    throw new HttpRequestException("simulated logout HTTP failure");
                }
                else if (_testSeam?.LogoutHttpSimulatedStatusCode is int simulatedStatus)
                {
                    logoutHttpSucceeded = simulatedStatus >= 200 && simulatedStatus < 300;
                    if (!logoutHttpSucceeded)
                    {
                        Trace.TraceWarning($"Logout HTTP failed during logout: {simulatedStatus}");
                    }
                }
                else
                {
                    using var response = await _http.PostAsync($"{_serverBase}/api/kiosk/sessions/{sessionForLogout}/logout", null);
                    logoutHttpSucceeded = response.IsSuccessStatusCode;
                    if (!logoutHttpSucceeded)
                    {
                        Trace.TraceWarning($"Logout HTTP failed during logout: {(int)response.StatusCode}");
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Logout HTTP failed during logout: {ex.Message}");
            }
            finally
            {
                _logoutInProgress = false;
            }
        }

        ShowLockLayout();
        if (!logoutHttpSucceeded)
        {
            _locallyLockedSessionId = Guid.Empty;
        }

        _sessionId = Guid.Empty;
        _runtime.BindSession(Guid.Empty);
        RefreshStationStatusText();
        try
        {
            RunSessionCleanup();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RunSessionCleanup failed during logout: {ex.Message}");
        }
    }

    public void ApplyHeartbeatDisplay(KioskDisplayDto? display)
    {
        if (display is null)
        {
            return;
        }

        _lastDisplay = display;
        _shell.Dispatcher.Invoke(RenderDisplay);
    }

    private void OnHeartbeatAcknowledged(KioskHeartbeatAck ack)
    {
        _lastHeartbeatAckUtc = DateTime.UtcNow;
        if (ack.CredentialsVersion > 0 && ack.CredentialsVersion != _credentialsVersion)
        {
            _credentialsVersion = ack.CredentialsVersion;
            _ = RefreshCredentialHashesAsync();
        }

        if (ack.KioskFreeUseIdleMinutes > 0)
        {
            _freeUseIdleMonitor.SetIdleMinutes(ack.KioskFreeUseIdleMinutes);
        }
        var phase = _orchestrator.OnHeartbeatAck(ack, _sessionId, _runtime.ClientMonotonicMs);
        ApplyHeartbeatDisplay(ack.Display);
        _ = DrainOfflineQueueAsync();
        _shell.Dispatcher.Invoke(() =>
        {
            RefreshStationStatusText();

            if (phase is KioskShellPhase.LeaseExpired or KioskShellPhase.Ended
                || ack.ManualSettle || string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                EnterLocalLock(phase == KioskShellPhase.LeaseExpired);
            }
            else if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
            {
                var ackLeaseSession = ack.Lease?.Payload.SessionId;
                var sameLockedSession = _locallyLockedSessionId != Guid.Empty
                    && ackLeaseSession is Guid leasedWhileLocked
                    && leasedWhileLocked != Guid.Empty
                    && leasedWhileLocked == _locallyLockedSessionId;
                if (sameLockedSession && phase == KioskShellPhase.Playing && !_logoutInProgress)
                {
                    _locallyLockedSessionId = Guid.Empty;
                }
                else if (_locallyLockedSessionId != Guid.Empty
                    && ackLeaseSession is Guid differentSession
                    && differentSession != Guid.Empty
                    && differentSession != _locallyLockedSessionId)
                {
                    _locallyLockedSessionId = Guid.Empty;
                    _sessionId = differentSession;
                    _runtime.BindSession(differentSession);
                    _ = ApplyStationProfileAsync();
                }
                else if (_sessionId == Guid.Empty
                    && ackLeaseSession is Guid leased
                    && leased != Guid.Empty
                    && _locallyLockedSessionId == Guid.Empty)
                {
                    _sessionId = leased;
                    _runtime.BindSession(leased);
                    _ = ApplyStationProfileAsync();
                }

                if (_sessionId != Guid.Empty && _locallyLockedSessionId == Guid.Empty && phase == KioskShellPhase.Playing && !_logoutInProgress)
                {
                    ShowPlayingLayout();
                    if (!_uiTick.IsEnabled)
                    {
                        _uiTick.Start();
                    }

                    if (!_replayTick.IsEnabled)
                    {
                        _replayTick.Start();
                    }
                }
            }

            var visible = _orchestrator.FilterVisibleGames(_catalogCache);
            var allow = visible.SelectMany(g => g.AllowedProcessNames).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (allow.Count > 0 && phase == KioskShellPhase.Playing)
            {
                _orchestrator.EnforceAllowlist(allow);
            }
        });
    }

    private void ShowPlayingLayout()
    {
        _lockPanel.Visibility = Visibility.Collapsed;
        _launcherPanel.Visibility = Visibility.Visible;
        _playingPanel.Visibility = Visibility.Visible;
        RenderGameList();
    }

    private void ShowLockLayout()
    {
        _playingPanel.Visibility = Visibility.Collapsed;
        _launcherPanel.Visibility = Visibility.Collapsed;
        _lockPanel.Visibility = Visibility.Visible;
    }

    private void RenderGameList()
    {
        if (_gameList is null)
        {
            return;
        }

        var query = (_gameSearch?.Text ?? "").Trim();
        var category = _gameCategory?.SelectedItem as string;
        var games = _orchestrator.FilterVisibleGames(_catalogCache).ToList();
        if (!string.IsNullOrEmpty(query))
        {
            games = games.Where(g => g.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        if (!string.IsNullOrEmpty(category) && category != "All")
        {
            if (int.TryParse(category, out var catId))
            {
                games = games.Where(g => g.CategoryId == catId).ToList();
            }
        }

        _visibleGames.Clear();
        _visibleGames.AddRange(games);
        _gameList.ItemsSource = games.Select(g => g.Name).ToList();
    }

    private void OnUiTick()
    {
        RenderDisplay();
        _shell.Dispatcher.Invoke(RefreshStationStatusText);
        var utc = DateTime.UtcNow;
        if (_operatorFreeUseId != Guid.Empty && utc - _lastAlivePersistUtc >= TimeSpan.FromSeconds(30))
        {
            _freeUseLocal.OpenFreeUseId = _operatorFreeUseId;
            _freeUseLocal.TouchAlive(utc);
            _lastAlivePersistUtc = utc;
            if (_freeUseIdleMonitor.ShouldAutoLock(utc))
            {
                _ = EndOperatorFreeUseAsync("auto_idle");
                _shell.Dispatcher.Invoke(() => EnterLocalLock(leaseExpired: false));
            }
            else if (_freeUseIdleMonitor.TryGetWarningCountdown(utc, out var warnSec))
            {
                _shell.Dispatcher.Invoke(() =>
                {
                    _freeBand.Text = $"{_operatorFreeUseMessage}\n{_freeUseIdleMonitor.FormatWarningText(warnSec)}";
                    _freeBand.Visibility = Visibility.Visible;
                });
            }
        }

        if (_locallyLockedSessionId != Guid.Empty || _sessionId == Guid.Empty)
        {
            return;
        }

        var transportDown = _sessionId != Guid.Empty && !_runtime.IsTransportConnected;
        var phase = _orchestrator.TickLease(_sessionId, _runtime.ClientMonotonicMs, DateTime.UtcNow, transportDown);
        if (phase == KioskShellPhase.LeaseExpired)
        {
            _shell.Dispatcher.Invoke(() => EnterLocalLock(leaseExpired: true));
        }
    }

    private void OnHeartbeatTransportFailed()
    {
        if (_orchestrator.Phase != KioskShellPhase.Playing || _sessionId == Guid.Empty)
        {
            return;
        }

        _orchestrator.EnqueuePoweredSegmentOnDisconnect(_sessionId, _runtime.ClientMonotonicMs);
    }

    private void OnRuntimeConnectionStatus(KioskTransportPhase phase, string detail)
    {
        _shell.Dispatcher.Invoke(() =>
        {
            try
            {
                if (phase == KioskTransportPhase.Connected)
                {
                    EnsureStatusUiTickRunning();
                    if (_runCatalogOnFirstConnected)
                    {
                        _runCatalogOnFirstConnected = false;
                        _ = RunStartupCatalogRefreshAsync();
                    }
                }

                RefreshStationStatusText();
            }
            catch
            {
                // UI status subscribers must not break transport reconnect.
            }
        });
    }

    private void EnsureStatusUiTickRunning()
    {
        if (_deviceId != Guid.Empty && !_uiTick.IsEnabled)
        {
            _uiTick.Start();
        }
    }

    private void RefreshStationStatusText()
    {
        if (string.IsNullOrEmpty(_stationId))
        {
            return;
        }

        if (_locallyLockedSessionId != Guid.Empty
            || (_sessionId == Guid.Empty && _lockPanel.Visibility == Visibility.Visible))
        {
            _status.Text = KioskStationStatus.FormatLocalLockScreen(
                _stationId,
                _locallyLockedSessionId != Guid.Empty && _localLockLeaseExpired,
                _runtime.TransportPhase,
                _runtime.IsTransportConnected,
                _runtime.SignalRAutoReconnecting,
                _pairingJustFailed,
                _runtime.ConsecutiveHeartbeatFailures,
                _lastHeartbeatAckUtc,
                DateTime.UtcNow,
                _runtime.LastTransportErrorDetail);
            return;
        }

        _status.Text = KioskStationStatus.Resolve(
            _stationId,
            _runtime.TransportPhase,
            _runtime.IsTransportConnected,
            _runtime.SignalRAutoReconnecting,
            _pairingJustFailed,
            _sessionId != Guid.Empty,
            _runtime.ConsecutiveHeartbeatFailures,
            _lastHeartbeatAckUtc,
            DateTime.UtcNow,
            _lastDisplay?.EstimatedRemainingSeconds,
            _runtime.LastTransportErrorDetail);
    }

    private void EnterLocalLock(bool leaseExpired)
    {
        if (_sessionId != Guid.Empty)
        {
            _locallyLockedSessionId = _sessionId;
        }

        _localLockLeaseExpired = leaseExpired;
        ShowLockLayout();
        RefreshStationStatusText();
        _replayTick.Stop();
        try
        {
            RunSessionCleanup();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"RunSessionCleanup failed during local lock: {ex.Message}");
        }
    }

    private async Task RunStartupCatalogRefreshAsync()
    {
        try
        {
            await ApplyStationProfileAsync();
            await RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            _shell.Dispatcher.Invoke(() =>
                _status.Text = $"Station {_stationId} — catalog refresh failed: {ex.Message}");
        }
    }

    private void RunSessionCleanup()
    {
        if (_testSeam?.SessionCleanupThrows == true)
        {
            throw new UnauthorizedAccessException("host test session cleanup failure");
        }

        _orchestrator.KillDeniedProcesses();
        _orchestrator.EndSessionCleanup();
        ClearKioskProfileScratch();
    }

    private static readonly string[] KioskProfileScratchRelativeDirs =
    {
        "Downloads",
        @"AppData\Local\Temp",
        @"AppData\Local\Microsoft\Windows\INetCache",
        @"AppData\Local\Microsoft\Edge\User Data\Default\Cache",
        @"AppData\Local\Google\Chrome\User Data\Default\Cache",
        @"AppData\Local\Mozilla\Firefox\Profiles",
    };

    private static void ClearKioskProfileScratch()
        => ClearKioskProfileScratchUnder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), KioskProfileScratchRelativeDirs);

    private static void ClearKioskProfileScratchUnder(string profileRoot, IEnumerable<string> relativeDirs)
    {
        foreach (var relative in relativeDirs)
        {
            var dir = Path.Combine(profileRoot, relative);
            if (!Directory.Exists(dir) || IsReparsePoint(dir))
            {
                continue;
            }

            DeleteTreeContentsBestEffort(dir);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.ReparsePoint) != 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void DeleteTreeContentsBestEffort(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                try
                {
                    File.Delete(file);
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        string[] subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(directory).ToArray();
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        foreach (var sub in subdirs)
        {
            if (IsReparsePoint(sub))
            {
                continue;
            }

            DeleteTreeContentsBestEffort(sub);
            try
            {
                Directory.Delete(sub, recursive: false);
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task ApplyStationProfileAsync()
    {
        if (_deviceId == Guid.Empty || string.IsNullOrEmpty(_stationId))
        {
            return;
        }

        try
        {
            var response = await _http.GetAsync($"{_serverBase}/api/kiosk/stations/{_stationId}/config");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var isTestMode = doc.RootElement.TryGetProperty("isTestMode", out var tm) && tm.GetBoolean();
            _orchestrator.ApplyStationConfig(isTestMode);
            _shell.Dispatcher.Invoke(() =>
            {
                if (isTestMode)
                {
                    _shell.WindowStyle = WindowStyle.SingleBorderWindow;
                    _shell.ResizeMode = ResizeMode.CanResize;
                    _shell.WindowState = WindowState.Normal;
                }
                else
                {
                    _shell.WindowStyle = WindowStyle.None;
                    _shell.ResizeMode = ResizeMode.NoResize;
                    _shell.WindowState = WindowState.Maximized;
                }
            });
        }
        catch
        {
            // keep default final mode when profile is unreachable
        }
    }

    private async Task RefreshCatalogAsync()
    {
        if (_deviceId == Guid.Empty)
        {
            return;
        }

        EnsureCatalogStore();
        var result = await CatalogRefreshMerger.RefreshAsync(
            async since =>
            {
                var response = await _http.GetAsync($"{_serverBase}/api/kiosk/catalog?sinceVersion={since}");
                if (!response.IsSuccessStatusCode)
                {
                    return new CatalogHttpOutcome(false, since, []);
                }

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var version = doc.RootElement.TryGetProperty("catalogVersion", out var cv) ? cv.GetInt64() : since;
                if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                {
                    return new CatalogHttpOutcome(true, version, []);
                }

                var items = entries.EnumerateArray().Select(CatalogRefreshMerger.ParseCatalogItem).ToList();
                return new CatalogHttpOutcome(true, version, items);
            },
            _catalogStore!,
            _catalogCache,
            _catalogVersion);
        _catalogCache.Clear();
        _catalogCache.AddRange(result.Items);
        _catalogVersion = result.Version;
        _shell.Dispatcher.Invoke(RenderGameList);
    }

    private async Task DrainOfflineQueueAsync()
    {
        if (_deviceId == Guid.Empty)
        {
            return;
        }

        try
        {
            await _orchestrator.DrainOfflineQueueAsync(async events =>
            {
                var payload = JsonSerializer.Serialize(events);
                var response = await _http.PostAsync(
                    $"{_serverBase}/api/kiosk/devices/events",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                if (!response.IsSuccessStatusCode)
                {
                    return Array.Empty<Guid>();
                }

                var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (!body.RootElement.TryGetProperty("ackedEventIds", out var ids))
                {
                    return Array.Empty<Guid>();
                }

                return ids.EnumerateArray().Select(e => e.GetGuid()).ToList();
            }, CancellationToken.None);
        }
        catch
        {
            // retry on next tick
        }
    }

    private void RenderDisplay()
    {
        if (_lastDisplay is null)
        {
            return;
        }

        _balance.Text = _lastDisplay.WalletBalanceToman is long w
            ? $"Wallet: {w:N0} تومان"
            : "";
        _elapsed.Text = _lastDisplay.ElapsedDisplaySeconds is long e
            ? $"Elapsed: {TimeSpan.FromSeconds(e):hh\\:mm\\:ss}"
            : "";
        _freeBand.Text = _lastDisplay.FreeBandText ?? "";
    }

    private void TryApplySetupServerBase()
    {
        if (KioskLegacyDevPath.IsActive())
        {
            return;
        }

        var setup = new KioskSetupStore().TryLoad();
        if (setup is { IsComplete: true } && !string.IsNullOrWhiteSpace(setup.ServerBase))
        {
            _serverBase = setup.ServerBase.TrimEnd('/');
        }
    }

    private async Task RefreshCredentialHashesAsync()
    {
        if (_deviceId == Guid.Empty || string.IsNullOrEmpty(_stationId))
        {
            return;
        }

        try
        {
            var response = await _http.GetAsync($"{_serverBase}/api/kiosk/stations/{_stationId}/config");
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var setup = new KioskSetupStore().TryLoad() ?? new KioskSetupDocument();
            if (doc.RootElement.TryGetProperty("kioskManagerHash", out var mh) && mh.ValueKind == JsonValueKind.String)
            {
                setup.KioskManagerHash = mh.GetString() ?? setup.KioskManagerHash;
            }

            if (doc.RootElement.TryGetProperty("kioskOperatorHash", out var oh) && oh.ValueKind == JsonValueKind.String)
            {
                setup.KioskOperatorHash = oh.GetString() ?? setup.KioskOperatorHash;
            }

            if (!string.IsNullOrEmpty(setup.KioskManagerHash))
            {
                setup.ServerBase = _serverBase;
                if (setup.CompletedAtUtc == default)
                {
                    setup.CompletedAtUtc = DateTimeOffset.UtcNow;
                }

                new KioskSetupStore().Save(setup);
            }
        }
        catch
        {
            // keep cached hashes until next ack
        }
    }

    private void LoadDeviceState()
    {
        var path = DeviceStorePath();
        if (!File.Exists(path))
        {
            return;
        }

        var json = File.ReadAllText(path);
        var doc = JsonDocument.Parse(json);
        _deviceId = doc.RootElement.GetProperty("deviceId").GetGuid();
        var loadedKey = doc.RootElement.GetProperty("deviceKey").GetString()!;
        OnDeviceCredentialsChanged(loadedKey);
        _deviceKey = loadedKey;
        _stationId = doc.RootElement.GetProperty("stationId").GetString()!;
        if (doc.RootElement.TryGetProperty("serverBase", out var savedBase))
        {
            _serverBase = savedBase.GetString() ?? _serverBase;
        }

        if (doc.RootElement.TryGetProperty("leaseVerifyPublicKeyPem", out var pem))
        {
            _leaseVerifyPublicKeyPem = pem.GetString() ?? "";
        }

        ApplyDeviceHeaders(_http);
    }

    private void ApplyDeviceHeaders(HttpClient http)
    {
        if (_deviceId == Guid.Empty)
        {
            return;
        }

        http.DefaultRequestHeaders.Remove(DeviceIdHeader);
        http.DefaultRequestHeaders.Remove(DeviceKeyHeader);
        http.DefaultRequestHeaders.Add(DeviceIdHeader, _deviceId.ToString());
        http.DefaultRequestHeaders.Add(DeviceKeyHeader, _deviceKey);
    }

    private void SaveDeviceState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceStorePath())!);
        var json = JsonSerializer.Serialize(new
        {
            deviceId = _deviceId,
            deviceKey = _deviceKey,
            stationId = _stationId,
            serverBase = _serverBase,
            leaseVerifyPublicKeyPem = _leaseVerifyPublicKeyPem,
        });
        File.WriteAllText(DeviceStorePath(), json);
        ApplyDeviceHeaders(_http);
    }

    private static string DeviceStorePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk", "device.json");

    private async Task EnsureRuntimeAsync()
    {
        if (string.IsNullOrEmpty(_stationId))
        {
            return;
        }

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(root);
        var queuePath = Path.Combine(root, "events.db");
        await _runtime.ResetForReconfigureAsync();
        _runtime.Configure(new Uri($"{_serverBase}/hubs/kiosk"), _deviceId, _deviceKey, _stationId, queuePath, legacyWindows: true);
        _orchestrator.BindOfflineQueue(_runtime.GetQueue());
        EnsureCatalogStore();
        await _runtime.StartAsync(CancellationToken.None);
    }

    private void EnsureCatalogStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(root);
        _catalogStore ??= new SqliteCatalogCache(Path.Combine(root, "catalog.db"));
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_stationId))
        {
            _status.Text = "Unpaired — use operator pairing code";
            return;
        }

        try
        {
            await EnsureRuntimeAsync();
            if (_runtime.IsTransportConnected)
            {
                await ApplyStationProfileAsync();
                await RefreshCatalogAsync();
                if (_sessionId == Guid.Empty)
                {
                    RefreshStationStatusText();
                }
            }
            else
            {
                _runCatalogOnFirstConnected = true;
                RefreshStationStatusText();
            }
        }
        catch (Exception ex)
        {
            _status.Text = $"Station {_stationId} — {ex.Message}";
        }
    }

    public async Task<string?> TryOperatorLockAsync()
    {
        if (_deviceId == Guid.Empty || string.IsNullOrEmpty(_stationId))
        {
            return "kiosk.oplock.err.unpaired";
        }

        if (_sessionId != Guid.Empty)
        {
            if (!KioskOperatorLockRules.CanLockOpenSessionOnline(_runtime.IsTransportConnected))
            {
                return KioskOperatorLockRules.OfflineSessionLockErrorKey;
            }

            var body = JsonSerializer.Serialize(new { sessionId = _sessionId });
            var response = await _http.PostAsync(
                $"{_serverBase}/api/kiosk/stations/{Uri.EscapeDataString(_stationId)}/operator-lock/session",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
            {
                return $"kiosk.oplock.err.http.{(int)response.StatusCode}";
            }

            _sessionId = Guid.Empty;
            _locallyLockedSessionId = Guid.Empty;
            EnterLocalLock(leaseExpired: false);
            return null;
        }

        if (_operatorFreeUseId != Guid.Empty)
        {
            await EndOperatorFreeUseAsync("manual");
            EnterLocalLock(leaseExpired: false);
        }

        return null;
    }

    public async Task<string?> TryOperatorUnlockAsync()
    {
        if (_deviceId == Guid.Empty || string.IsNullOrEmpty(_stationId))
        {
            return "kiosk.oplock.err.unpaired";
        }

        if (_sessionId != Guid.Empty)
        {
            return "kiosk.oplock.err.session_open";
        }

        if (_operatorFreeUseId != Guid.Empty)
        {
            return null;
        }

        if (!KioskOperatorLockRules.CanStartFreeUseOnline(_runtime.IsTransportConnected))
        {
            return KioskOperatorLockRules.OfflineFreeUseStartErrorKey;
        }

        var startBody = JsonSerializer.Serialize(new { actorRole = _lastAdminRole == KioskAdminRole.Manager ? "manager" : "operator" });
        var response = await _http.PostAsync(
            $"{_serverBase}/api/kiosk/stations/{Uri.EscapeDataString(_stationId)}/operator-lock/free-use/start",
            new StringContent(startBody, Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            return $"kiosk.oplock.err.http.{(int)response.StatusCode}";
        }

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _operatorFreeUseId = doc.RootElement.GetProperty("freeUseId").GetGuid();
        var now = DateTime.UtcNow;
        _freeUseLocal.OpenFreeUseId = _operatorFreeUseId;
        _freeUseLocal.FreeUseStartedUtc = now;
        _freeUseLocal.TouchAlive(now);
        _lastAlivePersistUtc = now;
        _operatorFreeUseMessage = KioskOpStrings.FreeBanner;
        _freeUseIdleMonitor.RecordInput(now);
        _shell.Dispatcher.Invoke(() =>
        {
            _freeBand.Text = _operatorFreeUseMessage;
            _freeBand.Visibility = Visibility.Visible;
            ShowLockLayout();
        });
        return null;
    }

    private async Task EndOperatorFreeUseAsync(string reason)
    {
        if (_operatorFreeUseId == Guid.Empty)
        {
            return;
        }

        var endedAt = DateTime.UtcNow;
        var freeUseId = _operatorFreeUseId;
        _operatorFreeUseId = Guid.Empty;
        _operatorFreeUseMessage = "";
        _freeUseLocal.OpenFreeUseId = null;
        _freeUseLocal.Save();

        if (_runtime.IsTransportConnected)
        {
            var body = JsonSerializer.Serialize(new { freeUseId, reason, endedAtUtc = endedAt });
            await _http.PostAsync(
                $"{_serverBase}/api/kiosk/stations/{Uri.EscapeDataString(_stationId)}/operator-lock/free-use/end",
                new StringContent(body, Encoding.UTF8, "application/json"));
        }
        else if (KioskOperatorLockRules.CanEndFreeUseWhileOffline)
        {
            _orchestrator.EnqueueFreeUseEnd(freeUseId, reason, endedAt);
            _replayTick.Start();
        }

        _shell.Dispatcher.Invoke(() =>
        {
            _freeBand.Text = "";
            _freeBand.Visibility = Visibility.Collapsed;
        });
    }

    public void NotifyAdminRole(KioskAdminRole role) => _lastAdminRole = role;

    private void OnDeviceCredentialsChanged(string deviceKey)
    {
        if (string.Equals(_loadedDeviceKey, deviceKey, StringComparison.Ordinal))
        {
            return;
        }

        _loadedDeviceKey = deviceKey;
        _pairingJustFailed = false;
        ClearCustomerLoginError();
    }

    private void ShowCustomerLoginError(string message)
    {
        _shell.Dispatcher.Invoke(() =>
        {
            if (_loginError is null)
            {
                return;
            }

            _loginError.Text = message;
            _loginError.Visibility = Visibility.Visible;
        });
    }

    private void ClearCustomerLoginError()
    {
        _shell.Dispatcher.Invoke(() =>
        {
            if (_loginError is null)
            {
                return;
            }

            _loginError.Text = "";
            _loginError.Visibility = Visibility.Collapsed;
        });
    }

    private static string EventsQueuePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk",
            "events.db");

    private async Task FinalizeInterruptedFreeUseOnBootAsync()
    {
        _freeUseLocal = KioskFreeUseLocalState.Load();
        if (_freeUseLocal.OpenFreeUseId is not Guid openId || openId == Guid.Empty)
        {
            return;
        }

        _operatorFreeUseId = openId;
        if (KioskOfflineQueueInspect.HasPendingFreeUseEnd(EventsQueuePath(), openId))
        {
            _replayTick.Start();
            await DrainOfflineQueueAsync();
            return;
        }

        var restartEnd = KioskFreeUseBootPolicy.RestartEndUtcIfNeeded(_freeUseLocal, hasQueuedRealEnd: false);
        if (restartEnd is null)
        {
            return;
        }

        var freeUseId = openId;
        _operatorFreeUseId = Guid.Empty;
        _freeUseLocal.OpenFreeUseId = null;
        _freeUseLocal.Save();
        if (_runtime.IsTransportConnected && _deviceId != Guid.Empty)
        {
            var body = JsonSerializer.Serialize(new { freeUseId, reason = "restart", endedAtUtc = restartEnd.Value });
            await _http.PostAsync(
                $"{_serverBase}/api/kiosk/stations/{Uri.EscapeDataString(_stationId)}/operator-lock/free-use/end",
                new StringContent(body, Encoding.UTF8, "application/json"));
        }
        else
        {
            _orchestrator.EnqueueFreeUseEnd(freeUseId, "restart", restartEnd.Value);
            _replayTick.Start();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _replayTick.Stop();
        await _runtime.DisposeAsync();
    }
}
