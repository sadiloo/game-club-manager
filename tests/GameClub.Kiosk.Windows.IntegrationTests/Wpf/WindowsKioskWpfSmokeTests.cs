#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

[Trait("Category", "WindowsWpf")]
public sealed class WindowsKioskWpfSmokeTests
{
    [Fact]
    public void wpf_main_window_starts_and_writes_screenshot_or_process_evidence()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var kioskPath = FindKioskExecutable(exeDir);
        var artifactDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "evidence");
        Directory.CreateDirectory(artifactDir);
        var screenshotPath = Path.Combine(artifactDir, "screenshot-lock.png");

        var inProcessOk = false;
        Exception inProcessError = null;
        var sta = new Thread(() =>
        {
            try
            {
                var app = new Application();
                var window = new GameClub.Kiosk.MainWindow();
                window.Show();
                window.UpdateLayout();
                var width = (int)Math.Max(window.ActualWidth, 800);
                var height = (int)Math.Max(window.ActualHeight, 600);
                var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var stream = File.Create(screenshotPath))
                {
                    encoder.Save(stream);
                }

                app.Shutdown();
                inProcessOk = true;
            }
            catch (Exception ex)
            {
                inProcessError = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromMinutes(1));

        if (inProcessOk && File.Exists(screenshotPath))
        {
            Assert.True(new FileInfo(screenshotPath).Length > 0);
            return;
        }

        Environment.SetEnvironmentVariable(
            "CHECKPOINT_SERVER_URL",
            Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_URL") ?? "http://127.0.0.1:5000");
        var psi = new ProcessStartInfo(kioskPath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(kioskPath)!,
        };
        using var process = Process.Start(psi);
        Thread.Sleep(TimeSpan.FromSeconds(8));
        File.WriteAllText(
            Path.Combine(artifactDir, "wpf-smoke.json"),
            $"{{\"inProcessOk\":{inProcessOk.ToString().ToLowerInvariant()},\"processRunning\":{(process is { HasExited: false }).ToString().ToLowerInvariant()},\"inProcessError\":\"{inProcessError?.Message}\"}}");
        Assert.NotNull(process);
        Assert.False(process.HasExited);
        process.Kill();
    }

    private static string FindKioskExecutable(string startDir)
    {
        var candidates = new[]
        {
            Path.Combine(startDir, "GameClub.Kiosk.exe"),
            Path.GetFullPath(Path.Combine(startDir, "..", "..", "..", "..", "..", "src", "GameClub.Kiosk", "bin", "Release", "net48", "GameClub.Kiosk.exe")),
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new FileNotFoundException("GameClub.Kiosk.exe not found for WPF smoke.");
    }
}
#endif
