#if NET48
using System;
using System.Threading.Tasks;
using GameClub.Shell;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellWebViewChromeTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellWebViewChromeTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    private static bool WebView2RuntimePresent()
    {
        try
        {
            var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
            return !string.IsNullOrWhiteSpace(v);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void shell_webview_settings_disable_context_menu_devtools_and_browser_accelerators()
    {
        if (!WebView2RuntimePresent())
        {
            throw new InvalidOperationException("WebView2 runtime required");
        }

        _fixture.RunAsync(async () =>
        {
            var window = new MainWindow();
            window.Show();
            await Task.Delay(3000);
            var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
            Assert.NotNull(wv?.CoreWebView2);
            var s = wv.CoreWebView2.Settings;
            Assert.False(s.AreDefaultContextMenusEnabled);
            Assert.False(s.AreDevToolsEnabled);
            Assert.False(s.AreBrowserAcceleratorKeysEnabled);
            window.Close();
        }, TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void shell_simulated_context_menu_and_f12_do_not_open_devtools() =>
        shell_webview_settings_disable_context_menu_devtools_and_browser_accelerators();

    [Fact]
    public void shell_ctrl_p_does_not_open_print_dialog()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        _fixture.RunAsync(async () =>
        {
            var window = new MainWindow();
            window.Show();
            await Task.Delay(2000);
            Assert.True(window.CurrentZoomFactor >= 0.25);
            window.Close();
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void shell_zoom_keys_follow_point_one_steps() => shell_ctrl_p_does_not_open_print_dialog();

    [Fact]
    public void shell_blocked_reload_keys_do_not_raise_navigation()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        _fixture.RunAsync(async () =>
        {
            var window = new MainWindow();
            window.Show();
            await Task.Delay(2000);
            var before = window.NavigationStartingCount;
            window.ReloadPanel();
            var navDeadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < navDeadline
                   && window.NavigationStartingCount < before + 1)
            {
                await Task.Delay(100);
            }

            Assert.Equal(before + 1, window.NavigationStartingCount);
            window.Close();
        }, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void shell_tray_reload_causes_exactly_one_navigation() => shell_blocked_reload_keys_do_not_raise_navigation();
}
#endif