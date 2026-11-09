using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameClub.Kiosk.Core.Catalog;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Services;

public sealed class KioskHostService : IAsyncDisposable
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    private readonly Window _shell;
    private readonly TextBlock _status;
    private readonly TextBlock _balance;
    private readonly TextBlock _package;
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
    private string _serverBase = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000";
    private string _leaseVerifyPublicKeyPem = "";
    private Guid _deviceId;
    private string _deviceKey = "";
    private string _stationId = "";
    private Guid _sessionId;
    private KioskDisplayDto? _lastDisplay;

    public string ServerBase => _serverBase;

    public KioskHostService(
        Window shell,
        TextBlock status,
        TextBlock balance,
        TextBlock package,
        TextBlock elapsed,
        TextBlock freeBand,
        UIElement lockPanel,
        UIElement launcherPanel,
        UIElement playingPanel,
        ListBox? gameList = null,
        TextBox? gameSearch = null,
        ComboBox? gameCategory = null)
    {
        _shell = shell;
        _status = status;
        _balance = balance;
        _package = package;
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
        if (!string.IsNullOrEmpty(_leaseVerifyPublicKeyPem))
        {
            _orchestrator.ConfigureLeaseVerifier(_leaseVerifyPublicKeyPem);
        }

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
            _status.Text = "Pairing failed";
            return;
        }

        var doc = JsonDocument.Parse(await pair.Content.ReadAsStringAsync());
        _deviceId = doc.RootElement.GetProperty("deviceId").GetGuid();
        _deviceKey = doc.RootElement.GetProperty("deviceKey").GetString()!;
        _stationId = doc.RootElement.GetProperty("stationId").GetString()!;
        if (doc.RootElement.TryGetProperty("leaseVerifyPublicKeyPem", out var pem))
        {
            _leaseVerifyPublicKeyPem = pem.GetString() ?? "";
            _orchestrator.ConfigureLeaseVerifier(_leaseVerifyPublicKeyPem);
        }

        SaveDeviceState();
        await EnsureRuntimeAsync();
        await RefreshCatalogAsync();
        _status.Text = $"Paired to {_stationId}";
    }

    public async Task LoginAsync(string loginName, string password)
    {
        if (_deviceId == Guid.Empty)
        {
            _status.Text = "Pair before login";
            return;
        }

        var response = await _http.PostAsync(
            $"{_serverBase}/api/kiosk/auth/login",
            new StringContent(JsonSerializer.Serialize(new { loginName, password, stationId = _stationId }), Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode)
        {
            _status.Text = "Login failed";
            return;
        }

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        _sessionId = doc.RootElement.GetProperty("sessionId").GetGuid();
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
        if (_sessionId != Guid.Empty)
        {
            await _http.PostAsync($"{_serverBase}/api/kiosk/sessions/{_sessionId}/logout", null);
        }

        _sessionId = Guid.Empty;
        _runtime.BindSession(Guid.Empty);
        RunSessionCleanup();
        ShowLockLayout();
        _uiTick.Stop();
        _replayTick.Stop();
        _status.Text = $"Locked — {_stationId}";
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
        var phase = _orchestrator.OnHeartbeatAck(ack, _sessionId, _runtime.ClientMonotonicMs);
        ApplyHeartbeatDisplay(ack.Display);
        _ = DrainOfflineQueueAsync();
        _shell.Dispatcher.Invoke(() =>
        {
            if (ack.Display?.EstimatedRemainingSeconds is long seconds)
            {
                _status.Text = $"Station {_stationId} — ~{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} left";
            }

            if (phase is KioskShellPhase.LeaseExpired or KioskShellPhase.Ended
                || ack.ManualSettle || string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                RunSessionCleanup();
                ShowLockLayout();
                _uiTick.Stop();
                _replayTick.Stop();
                _status.Text = phase == KioskShellPhase.LeaseExpired
                    ? $"Locked — lease expired ({_stationId})"
                    : $"Locked — {_stationId}";
            }
            else if (string.Equals(ack.SessionStatus, "Open", StringComparison.OrdinalIgnoreCase))
            {
                if (_sessionId == Guid.Empty && ack.Lease?.Payload.SessionId is Guid leased && leased != Guid.Empty)
                {
                    _sessionId = leased;
                    _runtime.BindSession(leased);
                }

                if (_sessionId != Guid.Empty)
                {
                    ShowPlayingLayout();
                    if (!_uiTick.IsEnabled)
                    {
                        _uiTick.Start();
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
        if (_sessionId == Guid.Empty)
        {
            return;
        }

        var phase = _orchestrator.TickLease(_sessionId, _runtime.ClientMonotonicMs, DateTime.UtcNow);
        if (phase == KioskShellPhase.LeaseExpired)
        {
            _shell.Dispatcher.Invoke(() =>
            {
                RunSessionCleanup();
                ShowLockLayout();
                _uiTick.Stop();
                _replayTick.Stop();
                _status.Text = $"Locked — lease expired ({_stationId})";
            });
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

    private void RunSessionCleanup()
    {
        _orchestrator.KillDeniedProcesses();
        _orchestrator.EndSessionCleanup();
        ClearKioskProfileScratch();
    }

    private static void ClearKioskProfileScratch()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var relative in new[] { "Downloads", @"AppData\Local\Temp", @"AppData\Local\Microsoft\Windows\INetCache" })
        {
            var dir = Path.Combine(root, relative);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // best-effort profile cleanup
                }
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
            _orchestrator.SetFinalMode(!isTestMode);
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

        try
        {
            EnsureCatalogStore();
            var since = _catalogVersion > 0 ? _catalogVersion : await _catalogStore!.LastVersionAsync();
            var response = await _http.GetAsync($"{_serverBase}/api/kiosk/catalog?sinceVersion={since}");
            if (!response.IsSuccessStatusCode)
            {
                var cached = await _catalogStore!.LoadAsync();
                if (cached.Count > 0)
                {
                    _catalogCache.Clear();
                    _catalogCache.AddRange(cached);
                    _shell.Dispatcher.Invoke(RenderGameList);
                }

                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var version = doc.RootElement.TryGetProperty("catalogVersion", out var cv) ? cv.GetInt64() : since;
            var entries = doc.RootElement.TryGetProperty("entries", out var ent) ? ent : default;
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            {
                if (since == 0)
                {
                    var cached = await _catalogStore!.LoadAsync();
                    _catalogCache.Clear();
                    _catalogCache.AddRange(cached);
                    _shell.Dispatcher.Invoke(RenderGameList);
                }

                return;
            }

            if (since > 0 && _catalogCache.Count > 0)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    var id = entry.GetProperty("id").GetInt32();
                    var idx = _catalogCache.FindIndex(g => g.Id == id);
                    var item = ParseCatalogItem(entry);
                    if (idx >= 0)
                    {
                        _catalogCache[idx] = item;
                    }
                    else
                    {
                        _catalogCache.Add(item);
                    }
                }
            }
            else
            {
                _catalogCache.Clear();
                foreach (var entry in entries.EnumerateArray())
                {
                    _catalogCache.Add(ParseCatalogItem(entry));
                }
            }

            _catalogVersion = version;
            await _catalogStore!.SaveAsync(_catalogCache, _catalogVersion);
            _shell.Dispatcher.Invoke(RenderGameList);
        }
        catch
        {
            // catalog fetch failures are surfaced via empty launcher list
        }
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
        _package.Text = _lastDisplay.PackageMinutesRemaining is { Count: > 0 } pk
            ? $"Package minutes: {string.Join(", ", pk)}"
            : "";
        _elapsed.Text = _lastDisplay.ElapsedDisplaySeconds is long e
            ? $"Elapsed: {TimeSpan.FromSeconds(e):hh\\:mm\\:ss}"
            : "";
        _freeBand.Text = _lastDisplay.FreeBandText ?? "";
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
        _deviceKey = doc.RootElement.GetProperty("deviceKey").GetString()!;
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
        _runtime.Configure(new Uri($"{_serverBase}/hubs/kiosk"), _deviceId, _deviceKey, _stationId, queuePath, legacyWindows: true);
        await _runtime.StartAsync(CancellationToken.None);
        _orchestrator.BindOfflineQueue(_runtime.GetQueue());
        EnsureCatalogStore();
    }

    private void EnsureCatalogStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk");
        Directory.CreateDirectory(root);
        _catalogStore ??= new SqliteCatalogCache(Path.Combine(root, "catalog.db"));
    }

    private static GameCatalogItem ParseCatalogItem(JsonElement entry)
        => new(
            entry.GetProperty("id").GetInt32(),
            entry.GetProperty("name").GetString() ?? "",
            entry.TryGetProperty("categoryId", out var c) ? c.GetInt32() : 1,
            entry.GetProperty("executableOrUri").GetString() ?? "",
            entry.TryGetProperty("arguments", out var a) ? a.GetString() : null,
            entry.TryGetProperty("iconUrl", out var i) ? i.GetString() : null,
            entry.TryGetProperty("sortOrder", out var o) ? o.GetInt32() : 0,
            entry.GetProperty("enabled").GetBoolean(),
            entry.TryGetProperty("allowedProcessNames", out var al)
                ? al.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [],
            entry.TryGetProperty("groupRestrictions", out var gr)
                ? gr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [],
            null);

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
            await RefreshCatalogAsync();
            _status.Text = $"Station {_stationId} — connected";
        }
        catch (Exception ex)
        {
            _status.Text = $"Station {_stationId} — {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync()
    {
        _replayTick.Stop();
        await _runtime.DisposeAsync();
    }
}
