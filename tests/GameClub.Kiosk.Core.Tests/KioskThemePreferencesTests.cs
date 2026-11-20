using GameClub.Kiosk.Core.Setup;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskThemePreferencesTests
{
    [Fact]
    public void roundtrip_and_corrupt_defaults_dark()
    {
        var path = Path.Combine(Path.GetTempPath(), "gcm-theme-" + Guid.NewGuid().ToString("N") + ".json");
        var prefs = new KioskThemePreferences(path);
        Assert.Equal(KioskVisualTheme.Dark, prefs.LoadOrDefault());
        prefs.Save(KioskVisualTheme.Light);
        Assert.Equal(KioskVisualTheme.Light, prefs.LoadOrDefault());
        File.WriteAllText(path, "{not json");
        Assert.Equal(KioskVisualTheme.Dark, prefs.LoadOrDefault());
    }

    [Fact]
    public void toggle_target_and_automation_names()
    {
        Assert.Equal(KioskVisualTheme.Light, KioskThemeStrings.TargetThemeAfterToggle(KioskVisualTheme.Dark));
        Assert.Equal("تم روشن", KioskThemeStrings.AutomationNameFor(KioskVisualTheme.Dark));
        Assert.Equal("تم تیره", KioskThemeStrings.AutomationNameFor(KioskVisualTheme.Light));
        Assert.Equal("\uE706", KioskThemeStrings.IconGlyphFor(KioskVisualTheme.Dark));
        Assert.Equal("\uE708", KioskThemeStrings.IconGlyphFor(KioskVisualTheme.Light));
    }
}