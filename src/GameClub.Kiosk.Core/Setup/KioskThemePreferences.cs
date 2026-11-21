using System.Text.Json;

namespace GameClub.Kiosk.Core.Setup;

public sealed class KioskThemePreferences
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string Path { get; }

    public KioskThemePreferences(string? path = null)
        => Path = path ?? System.IO.Path.Combine(KioskSetupPaths.KioskDataRoot, "theme.local.json");

    public KioskVisualTheme LoadOrDefault()
    {
        if (!File.Exists(Path))
        {
            return KioskVisualTheme.Dark;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ThemeDto>(File.ReadAllText(Path), JsonOptions);
            return dto?.Theme?.ToLowerInvariant() switch
            {
                "light" => KioskVisualTheme.Light,
                "dark" => KioskVisualTheme.Dark,
                _ => KioskVisualTheme.Dark,
            };
        }
        catch (JsonException)
        {
            return KioskVisualTheme.Dark;
        }
        catch (IOException)
        {
            return KioskVisualTheme.Dark;
        }
    }

    public void Save(KioskVisualTheme theme)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var name = theme == KioskVisualTheme.Light ? "light" : "dark";
        File.WriteAllText(Path, JsonSerializer.Serialize(new ThemeDto { Theme = name }, JsonOptions));
    }

    private sealed class ThemeDto
    {
        public string? Theme { get; set; }
    }
}