using System.Windows;
using GameClub.Kiosk.Core.Setup;
using KioskVisualTheme = GameClub.Kiosk.Core.Setup.KioskVisualTheme;

namespace GameClub.Kiosk.Themes;

/// <summary>
/// Swaps merged theme dictionaries (§0.4). Default is dark; no user toggle in MVP-B.
/// </summary>
public static class KioskThemeResources
{
    private static KioskVisualTheme _current = KioskVisualTheme.Dark;

    public static KioskVisualTheme Current
    {
        get => _current;
        set
        {
            if (_current == value)
            {
                return;
            }

            _current = value;
            ApplyToApplicationResources();
        }
    }

    public static void ApplyInstallDefault()
    {
        _current = new KioskThemePreferences().LoadOrDefault();
        ApplyToApplicationResources();
    }

    public static void ToggleAndPersist()
    {
        _current = KioskThemeStrings.TargetThemeAfterToggle(_current);
        new KioskThemePreferences().Save(_current);
        ApplyToApplicationResources();
    }

    public static void ApplyToApplicationResources()
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        var source = _current == KioskVisualTheme.Dark ? "Themes/Dark.xaml" : "Themes/Light.xaml";
        var theme = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(theme);
    }
}