using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Core.Guard;

/// <summary>Atomic verified-state persistence for Guard (brief §6.4).</summary>
public static class InternetVerifiedStateFile
{
    public static InternetCommandPayload? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<InternetCommandPayload>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, InternetCommandPayload payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(payload);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }
}