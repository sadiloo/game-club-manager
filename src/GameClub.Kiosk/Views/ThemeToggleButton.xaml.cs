using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using GameClub.Kiosk.Core.Setup;
using GameClub.Kiosk.Themes;

namespace GameClub.Kiosk.Views;

public partial class ThemeToggleButton : UserControl
{
    public ThemeToggleButton()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshChrome();
    }

    public void RefreshChrome()
    {
        var theme = KioskThemeResources.Current;
        Toggle.ToolTip = KioskThemeStrings.AutomationNameFor(theme);
        AutomationProperties.SetName(Toggle, KioskThemeStrings.AutomationNameFor(theme));
        Icon.Text = KioskThemeStrings.IconGlyphFor(theme);
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        KioskThemeResources.ToggleAndPersist();
        RefreshChrome();
    }
}