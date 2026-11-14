#if NET48
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameClub.Billing;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

[Trait("Category", "WindowsWpf")]
[Collection("windows-wpf-sta")]
public sealed class WindowsKioskWpfSmokeTests
{
    private readonly WpfStaApplicationFixture _wpf;

    public WindowsKioskWpfSmokeTests(WpfStaApplicationFixture wpf) => _wpf = wpf;

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

        _wpf.RunAsync(async () =>
        {
            try
            {
                var window = new GameClub.Kiosk.MainWindow();
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var lockPanel = (FrameworkElement)GetField(window, "LockPanel");
                    var status = (System.Windows.Controls.TextBlock)GetField(window, "StatusText");
                    var playingPanel = (FrameworkElement)GetField(window, "PlayingPanel");
                    lockVisible = lockPanel.Visibility;
                    statusText = status.Text;
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

                    await Task.CompletedTask;
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                inProcessError = ex;
            }
        }, TimeSpan.FromMinutes(1));

        if (inProcessError is not null)
        {
            Assert.Fail(inProcessError.Message);
        }

        Assert.Equal(Visibility.Visible, lockVisible);
        Assert.False(string.IsNullOrWhiteSpace(statusText));
        Assert.True(new FileInfo(screenshotPath).Length > 0);
    }

    [Fact]
    public void wpf_remaining_time_matches_funded_session_within_one_tick()
    {
        var setupError = (Exception)null;
        var staSetup = new Thread(() =>
        {
            try
            {
                WpfLiveServerTestHelper.SetupFundedSessionAsync("fund48667", "pass").GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                setupError = ex;
            }
        });
        staSetup.Start();
        staSetup.Join(TimeSpan.FromMinutes(2));
        if (setupError is not null)
        {
            Assert.Fail(setupError.ToString());
        }

        const int expectedSeconds = 2_160;
        const long startWalletToman = 48_667;
        string capturedStatus = null;
        Exception inProcessError = null;
        var at = DateTime.SpecifyKind(new DateTime(2026, 6, 3, 14, 0, 0), DateTimeKind.Unspecified);
        var card = new RateCardBuilder().AddBase("pc-normal", PlayMode.Offline, 1, 80_000).Build();
        var account = new PcAccount("fund48667", startWalletToman, Array.Empty<TimePackage>());
        var estimatorSeconds = (int)TimeSpan.FromMinutes(
            RemainingTimeEstimator.Estimate(account, "pc-normal", PlayMode.Offline, 1, at, card, graceMinuteEnabled: true).MoneyMinutes!.Value).TotalSeconds;
        Assert.Equal(expectedSeconds, estimatorSeconds);

        _wpf.RunAsync(async () =>
        {
            try
            {
                var window = new GameClub.Kiosk.MainWindow();
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    var host = GetField(window, "_host");
                    var login = host.GetType().GetMethod("LoginAsync", BindingFlags.Instance | BindingFlags.Public);
                    await (Task)login.Invoke(host, new object[] { "fund48667", "pass" });
                    var status = (System.Windows.Controls.TextBlock)GetField(window, "StatusText");
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                        var text = status.Text ?? "";
                        if (text.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            capturedStatus = text;
                            break;
                        }

                        await Task.Delay(1000);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                inProcessError = ex;
            }
        }, TimeSpan.FromMinutes(2));

        if (inProcessError is not null)
        {
            Assert.Fail(inProcessError.ToString());
        }

        Assert.False(string.IsNullOrWhiteSpace(capturedStatus), "StatusText must show server-driven remaining time (not test-written).");
        var match = Regex.Match(capturedStatus, @"~(\d{2}):(\d{2}):(\d{2})\s+left", RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"StatusText must match hh:mm:ss left pattern; got '{capturedStatus}'.");
        var displayedSeconds = (int)new TimeSpan(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)).TotalSeconds;
        Assert.InRange(displayedSeconds, expectedSeconds - 1, expectedSeconds + 1);
    }

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;
}
#endif
