using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Services;

public sealed class KioskHostService : IAsyncDisposable
{
    public const string DeviceIdHeader = "X-Device-Id";
    public const string DeviceKeyHeader = "X-Device-Key";

    private readonly Window _shell;
    private readonly TextBlock _status;
    private readonly HttpClient _http = new();
    private readonly KioskRuntime _runtime;
    private string _serverBase = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000";
    private Guid _deviceId;
    private string _deviceKey = "";
    private string _stationId = "";

    public KioskHostService(Window shell, TextBlock status)
    {
        _shell = shell;
        _status = status;
        _runtime = new KioskRuntime(() => CreateHttpClient());
        _http.DefaultRequestHeaders.Add("User-Agent", "CheckpointKiosk/1.0");
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
        var json = JsonSerializer.Serialize(new { deviceId = _deviceId, deviceKey = _deviceKey, stationId = _stationId });
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
        _ = _runtime.GetQueue();
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
