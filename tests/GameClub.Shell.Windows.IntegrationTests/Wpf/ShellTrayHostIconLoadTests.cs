#if NET48
using System;
using System.Drawing;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellTrayHostIconLoadTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellTrayHostIconLoadTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData("tray-stopped.ico")]
    [InlineData("tray-green.ico")]
    [InlineData("tray-yellow.ico")]
    [InlineData("tray-red.ico")]
    public void shell_tray_pack_icons_load_non_null_under_test_host(string iconFile)
    {
        _fixture.RunAsync(() =>
        {
            Icon? icon = null;
            try
            {
                icon = ShellTrayHost.LoadTrayIcon(iconFile);
                Assert.NotNull(icon);
            }
            finally
            {
                icon?.Dispose();
            }

            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(15));
    }
}
#endif