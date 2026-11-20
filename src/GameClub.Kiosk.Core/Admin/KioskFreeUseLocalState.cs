using System.Text.Json;

namespace GameClub.Kiosk.Core.Admin;

public sealed class KioskFreeUseLocalState
{
    public Guid? OpenFreeUseId { get; set; }
    public DateTime LastAliveUtc { get; set; }
    public DateTime FreeUseStartedUtc { get; set; }

    public static string DefaultPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk",
            "free-use-local.json");

    public static KioskFreeUseLocalState Load(string? path = null)
    {
        path ??= DefaultPath();
        if (!File.Exists(path))
        {
            return new KioskFreeUseLocalState { LastAliveUtc = DateTime.UtcNow };
        }

        try
        {
            return JsonSerializer.Deserialize<KioskFreeUseLocalState>(File.ReadAllText(path))
                ?? new KioskFreeUseLocalState { LastAliveUtc = DateTime.UtcNow };
        }
        catch
        {
            return new KioskFreeUseLocalState { LastAliveUtc = DateTime.UtcNow };
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath();
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void TouchAlive(DateTime utc)
    {
        LastAliveUtc = utc;
        Save();
    }
}

public static class KioskFreeUseBootPolicy
{
    /// <summary>Returns restart end timestamp when no queued end should run first.</summary>
    public static DateTime? RestartEndUtcIfNeeded(KioskFreeUseLocalState state, bool hasQueuedRealEnd)
    {
        if (hasQueuedRealEnd || state.OpenFreeUseId is null || state.OpenFreeUseId == Guid.Empty)
        {
            return null;
        }

        return state.LastAliveUtc;
    }
}