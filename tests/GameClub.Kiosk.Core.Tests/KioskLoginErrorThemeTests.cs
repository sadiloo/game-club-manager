namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskLoginErrorThemeTests
{
    [Fact]
    public void main_window_login_error_uses_theme_danger_brush()
    {
        var xamlPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "GameClub.Kiosk", "MainWindow.xaml"));
        Assert.True(File.Exists(xamlPath), xamlPath);
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains("x:Name=\"LoginError\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Kiosk.DangerBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("#C41B40", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("#FF6F86", xaml, StringComparison.OrdinalIgnoreCase);
    }
}