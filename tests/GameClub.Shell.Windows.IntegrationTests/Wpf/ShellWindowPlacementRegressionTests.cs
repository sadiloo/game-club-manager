#if NET48
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using GameClub.Shell;
using GameClub.Shell.Shell;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellWindowPlacementRegressionTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellWindowPlacementRegressionTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_window_json_infinity_input_round_trips_to_default_size_and_valid_json()
    {
        var root = AllocateShellDataRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                ShellPaths.WindowJsonPath,
                """{"Left":0,"Top":0,"Width":Infinity,"Height":Infinity,"IsMaximized":true}""");

            _fixture.RunAsync(() =>
            {
                var window = new MainWindow();
                window.Show();
                Assert.Equal(WindowState.Maximized, window.WindowState);
                window.Close();

                var saved = File.ReadAllText(ShellPaths.WindowJsonPath);
                Assert.DoesNotContain("Infinity", saved, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("NaN", saved, StringComparison.OrdinalIgnoreCase);

                using var doc = JsonDocument.Parse(saved);
                var w = doc.RootElement.GetProperty("Width").GetDouble();
                var h = doc.RootElement.GetProperty("Height").GetDouble();
                Assert.True(!double.IsNaN(w) && !double.IsInfinity(w) && w > 0);
                Assert.True(!double.IsNaN(h) && !double.IsInfinity(h) && h > 0);
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(20));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_window_json_corrupt_file_falls_back_to_default_without_throw()
    {
        var root = AllocateShellDataRoot();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(ShellPaths.WindowJsonPath, "{not-json");

            _fixture.RunAsync(() =>
            {
                var window = new MainWindow();
                window.Show();
                // CI runner work area is 1024×728; effective size follows WindowPlacementStore work-area clamp.
                var area = Screen.PrimaryScreen!.WorkingArea;
                Assert.Equal(Math.Min(1280, area.Width), window.Width, 0);
                Assert.Equal(Math.Min(800, area.Height), window.Height, 0);
                window.Close();
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(20));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    private static string AllocateShellDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gcm-shell-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", root);
        return root;
    }

    private static void CleanupShellDataRoot(string root)
    {
        Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", null);
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
        }
    }
}
#endif