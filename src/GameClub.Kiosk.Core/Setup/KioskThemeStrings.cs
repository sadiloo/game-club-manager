namespace GameClub.Kiosk.Core.Setup;

public static class KioskThemeStrings
{
    public const string ToLight = "تم روشن";

    public const string ToDark = "تم تیره";

    public static string AutomationNameFor(KioskVisualTheme current)
        => current == KioskVisualTheme.Dark ? ToLight : ToDark;

    public static string IconGlyphFor(KioskVisualTheme current)
        => current == KioskVisualTheme.Dark ? "\uE706" : "\uE708";

    public static KioskVisualTheme TargetThemeAfterToggle(KioskVisualTheme current)
        => current == KioskVisualTheme.Dark ? KioskVisualTheme.Light : KioskVisualTheme.Dark;
}