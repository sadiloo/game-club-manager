#if NET48
using System;
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
    public void wpf_lock_screen_and_remaining_time_visible()
    {
        var artifactDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "evidence");
        Directory.CreateDirectory(artifactDir);
        var screenshotPath = Path.Combine(artifactDir, "screenshot-lock.png");

        Visibility lockVisible = Visibility.Collapsed;
        string statusText = null;
        Exception inProcessError = null;

        var sta = new Thread(() =>
        {
            try
            {
                var app = new Application();
                var window = new GameClub.Kiosk.MainWindow();
                window.Show();
                window.UpdateLayout();
                var lockPanel = (FrameworkElement)GetField(window, "LockPanel");
                var status = (System.Windows.Controls.TextBlock)GetField(window, "StatusText");
                var playingPanel = (FrameworkElement)GetField(window, "PlayingPanel");
                var displayBalance = (System.Windows.Controls.TextBlock)GetField(window, "DisplayBalance");
                var displayElapsed = (System.Windows.Controls.TextBlock)GetField(window, "DisplayElapsed");
                lockVisible = lockPanel.Visibility;
                statusText = status.Text;
                playingPanel.Visibility = Visibility.Visible;
                displayBalance.Text = "5:00";
                displayElapsed.Text = "00:05:00";
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
            }
            catch (Exception ex)
            {
                inProcessError = ex;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromMinutes(1));

        if (inProcessError is not null)
        {
            Assert.Fail(inProcessError.Message);
        }

        Assert.Equal(Visibility.Visible, lockVisible);
        Assert.False(string.IsNullOrWhiteSpace(statusText));
        Assert.True(new FileInfo(screenshotPath).Length > 0);
    }

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;
}
#endif
