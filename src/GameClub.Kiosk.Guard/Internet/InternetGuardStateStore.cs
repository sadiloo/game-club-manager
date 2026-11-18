using System;
using System.IO;
using System.Text.Json;
using GameClub.Kiosk.Core.Protocol;

namespace GameClub.Kiosk.Guard.Internet;

internal sealed class InternetGuardStateStore
{
    public InternetCommandPayload? Load()
    {
        try
        {
            if (!File.Exists(InternetGuardPaths.StatePath))
            {
                return null;
            }

            var json = File.ReadAllText(InternetGuardPaths.StatePath);
            return JsonSerializer.Deserialize<InternetCommandPayload>(json);
        }
        catch
        {
            return null;
        }
    }

    public void SaveVerified(InternetCommandPayload payload)
    {
        Directory.CreateDirectory(InternetGuardPaths.ProgramDataGuard);
        var json = JsonSerializer.Serialize(payload);
        var temp = InternetGuardPaths.StatePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(InternetGuardPaths.StatePath))
        {
            File.Replace(temp, InternetGuardPaths.StatePath, null);
        }
        else
        {
            File.Move(temp, InternetGuardPaths.StatePath);
        }
    }
}