using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Offline;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Services;

public sealed class KioskHostService
{
    private readonly Window _shell;
    private readonly TextBlock _status;
    private readonly HttpClient _http = new();
    private string _serverBase = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000";
    private Guid _deviceId;
    private string _deviceKey = "";
    private string _stationId = "";

    public KioskHostService(Window shell, TextBlock status)
    {
        _shell = shell;
        _status = status;
        _http.DefaultRequestHeaders.Add("User-Agent", "CheckpointKiosk/1.0");
        LoadDeviceState();
        _ = RefreshAsync();
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
        _http.DefaultRequestHeaders.Remove("X-Kiosk-Device-Id");
        _http.DefaultRequestHeaders.Remove("X-Kiosk-Device-Key");
        _http.DefaultRequestHeaders.Add("X-Kiosk-Device-Id", _deviceId.ToString());
        _http.DefaultRequestHeaders.Add("X-Kiosk-Device-Key", _deviceKey);
    }

    private void SaveDeviceState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DeviceStorePath())!);
        var json = JsonSerializer.Serialize(new { deviceId = _deviceId, deviceKey = _deviceKey, stationId = _stationId });
        File.WriteAllText(DeviceStorePath(), json);
        _http.DefaultRequestHeaders.Remove("X-Kiosk-Device-Id");
        _http.DefaultRequestHeaders.Remove("X-Kiosk-Device-Key");
        _http.DefaultRequestHeaders.Add("X-Kiosk-Device-Id", _deviceId.ToString());
        _http.DefaultRequestHeaders.Add("X-Kiosk-Device-Key", _deviceKey);
    }

    private static string DeviceStorePath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Checkpoint", "kiosk", "device.json");

    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(_stationId))
        {
            _status.Text = "Unpaired — use operator pairing code";
            return;
        }

        _status.Text = $"Station {_stationId} — kiosk runtime active";
        var queue = new SqliteOfflineEventQueue(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk",
            "events.db"));
        _ = queue;
        await Task.Delay(500);
    }
}
