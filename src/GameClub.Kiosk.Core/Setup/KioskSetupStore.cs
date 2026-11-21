using System.Text.Json;
using GameClub.Kiosk.Core.Security;

namespace GameClub.Kiosk.Core.Setup;

public sealed class KioskSetupStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly IKioskTrustFileClient _trustWriter;

    public KioskSetupStore(string? path = null, IKioskTrustFileClient? trustWriter = null)
    {
        _path = path ?? KioskSetupPaths.SetupJsonPath;
        _trustWriter = trustWriter ?? KioskTrustFileClientFactory.ForPath(_path);
    }

    public bool ShouldShowFirstRun() =>
        !KioskLegacyDevPath.IsActive() && !(TryLoad()?.IsComplete ?? false);

    public KioskSetupDocument? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<KioskSetupDocument>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(KioskSetupDocument document)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(document, JsonOptions);
        _trustWriter.WriteTrustFile(_path, json);
    }
}