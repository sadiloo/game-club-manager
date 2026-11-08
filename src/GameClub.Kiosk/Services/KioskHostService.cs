using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Protocol;
using GameClub.Kiosk.Core.Runtime;

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
    private readonly UIElement _playingPanel;
    private readonly HttpClient _http = new();
    private readonly KioskRuntime _runtime;
    private readonly DispatcherTimer _uiTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _serverBase = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000";
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
        UIElement playingPanel)
    {
        _shell = shell;
        _status = status;
        _balance = balance;
        _package = package;
        _elapsed = elapsed;
        _freeBand = freeBand;
        _lockPanel = lockPanel;
        _playingPanel = playingPanel;
        _runtime = new KioskRuntime(() => CreateHttpClient());
        _runtime.HeartbeatAcknowledged += OnHeartbeatAcknowledged;
        _http.DefaultRequestHeaders.Add("User-Agent", "CheckpointKiosk/1.0");
        _uiTick.Tick += (_, _) => RenderDisplay();
        LoadDeviceState();
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
        var expected = Environment.GetEnvironmentVariable("CHECKPOINT_ADMIN_PASSWORD") ?? "checkpoint-admin";
        if (!string.Equals(password, expected, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
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
        SaveDeviceState();
        await EnsureRuntimeAsync();
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
        _lockPanel.Visibility = Visibility.Collapsed;
        _playingPanel.Visibility = Visibility.Visible;
        _uiTick.Start();
        _status.Text = $"Playing on {_stationId}";
    }

    public async Task LogoutAsync()
    {
        if (_sessionId != Guid.Empty)
        {
            await _http.PostAsync($"{_serverBase}/api/kiosk/sessions/{_sessionId}/logout", null);
        }

        _sessionId = Guid.Empty;
        _runtime.BindSession(Guid.Empty);
        _playingPanel.Visibility = Visibility.Collapsed;
        _lockPanel.Visibility = Visibility.Visible;
        _uiTick.Stop();
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
        ApplyHeartbeatDisplay(ack.Display);
        _shell.Dispatcher.Invoke(() =>
        {
            if (ack.Display?.EstimatedRemainingSeconds is long seconds)
            {
                _status.Text = $"Station {_stationId} — ~{TimeSpan.FromSeconds(seconds):hh\\:mm\\:ss} left";
            }

            if (ack.ManualSettle || string.Equals(ack.SessionStatus, "AwaitingPayment", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ack.SessionStatus, "Ended", StringComparison.OrdinalIgnoreCase))
            {
                _playingPanel.Visibility = Visibility.Collapsed;
                _lockPanel.Visibility = Visibility.Visible;
                _uiTick.Stop();
                _status.Text = $"Locked — {_stationId}";
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
                    _lockPanel.Visibility = Visibility.Collapsed;
                    _playingPanel.Visibility = Visibility.Visible;
                }
            }
        });
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
        var json = JsonSerializer.Serialize(new { deviceId = _deviceId, deviceKey = _deviceKey, stationId = _stationId, serverBase = _serverBase });
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

        var queuePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk",
            "events.db");
        _runtime.Configure(new Uri($"{_serverBase}/hubs/kiosk"), _deviceId, _deviceKey, _stationId, queuePath, legacyWindows: true);
        await _runtime.StartAsync(CancellationToken.None);
        _runtime.GetQueue();
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
            _status.Text = $"Station {_stationId} — connected";
        }
        catch (Exception ex)
        {
            _status.Text = $"Station {_stationId} — {ex.Message}";
        }
    }

    public async ValueTask DisposeAsync() => await _runtime.DisposeAsync();
}
