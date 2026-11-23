using System.Text.Json;
using GameClub.Kiosk.Core.Security;
using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Core.Admin;

public sealed class KioskAdminLockStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly KioskAdminLock _lock = new();
    private readonly IKioskTrustFileClient _trustWriter;

    public KioskAdminLockStore(string? path = null, IKioskTrustFileClient? trustWriter = null)
    {
        _path = path ?? Path.Combine(KioskSetupPaths.KioskDataRoot, "admin-lock.json");
        _trustWriter = trustWriter ?? KioskTrustFileClientFactory.ForPath(_path);
    }

    public KioskAdminLock Lock
    {
        get
        {
            EnsureLoaded();
            return _lock;
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(_lock.Snapshot().Values, JsonOptions);
        _trustWriter.WriteTrustFile(_path, json);
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var states = JsonSerializer.Deserialize<List<KioskAdminLockState>>(File.ReadAllText(_path), JsonOptions);
            if (states is { Count: > 0 })
            {
                _lock.Restore(states);
            }
        }
        catch (JsonException)
        {
        }
    }

    private bool _loaded;
}