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
using GameClub.Kiosk.Core.Runtime;
using GameClub.Kiosk.Services;
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
                var loginName = Environment.GetEnvironmentVariable("WPF_STABILITY_LOGIN") ?? "fund48667";
                WpfLiveServerTestHelper.SetupFundedSessionAsync(loginName, "pass").GetAwaiter().GetResult();
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
        string lastPollStatusText = null;
        string pollFailureDiagnostics = null;
        Exception inProcessError = null;
        var at = DateTime.SpecifyKind(new DateTime(2026, 6, 3, 14, 0, 0), DateTimeKind.Unspecified);
        var card = new RateCardBuilder().AddBase("pc-normal", PlayMode.Offline, 1, 80_000).Build();
        var stabilityLogin = Environment.GetEnvironmentVariable("WPF_STABILITY_LOGIN") ?? "fund48667";
        var account = new PcAccount(stabilityLogin, startWalletToman);
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
                    await WpfLiveServerTransportHelper.WaitForKioskRuntimeTransportConnectedAsync(host, TimeSpan.FromSeconds(60));
                    var login = host.GetType().GetMethod("LoginAsync", BindingFlags.Instance | BindingFlags.Public);
                    await (Task)login.Invoke(host, new object[] { stabilityLogin, "pass" });
                    var status = (System.Windows.Controls.TextBlock)GetField(window, "StatusText");
                    var pollStartedUtc = DateTime.UtcNow;
                    var deadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < deadline)
                    {
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                        var text = KioskStatusInlineApplicator.ReadCombinedText(status);
                        lastPollStatusText = text;
                        if (text.Contains("مانده", StringComparison.Ordinal))
                        {
                            capturedStatus = text;
                            break;
                        }

                        await Task.Delay(1000);
                    }

                    var elapsedPollingSec = (DateTime.UtcNow - pollStartedUtc).TotalSeconds;
                    window.Dispatcher.Invoke(() =>
                    {
                        pollFailureDiagnostics = BuildRemainingTimePollDiagnostics(
                            window,
                            lastPollStatusText,
                            elapsedPollingSec);
                    }, DispatcherPriority.Send);
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

        var statusAssertMessage = "StatusText must show server-driven remaining time (not test-written).";
        if (!string.IsNullOrEmpty(pollFailureDiagnostics))
        {
            statusAssertMessage += " " + pollFailureDiagnostics;
        }

        Assert.False(string.IsNullOrWhiteSpace(capturedStatus), statusAssertMessage);
        Assert.Contains("مانده", capturedStatus, StringComparison.Ordinal);
        var match = Regex.Match(
            capturedStatus,
            @"([\u06F0-\u06F9]{2}):([\u06F0-\u06F9]{2}):([\u06F0-\u06F9]{2})",
            RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"StatusText must match Persian hh:mm:ss in time-left line; got '{capturedStatus}'.");
        static int PersianDigitToInt(char c) => c - '\u06F0';
        var displayedSeconds = (int)new TimeSpan(
            PersianDigitToInt(match.Groups[1].Value[0]) * 10 + PersianDigitToInt(match.Groups[1].Value[1]),
            PersianDigitToInt(match.Groups[2].Value[0]) * 10 + PersianDigitToInt(match.Groups[2].Value[1]),
            PersianDigitToInt(match.Groups[3].Value[0]) * 10 + PersianDigitToInt(match.Groups[3].Value[1])).TotalSeconds;
        Assert.InRange(displayedSeconds, expectedSeconds - 1, expectedSeconds + 1);
    }

    private static object GetField(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;

    private static string BuildRemainingTimePollDiagnostics(
        GameClub.Kiosk.MainWindow window,
        string lastStatusText,
        double elapsedPollingSec)
    {
        object host = null;
        try
        {
            host = GetField(window, "_host");
        }
        catch
        {
            host = null;
        }

        var runtime = ReflectField(host, "_runtime");
        var orchestrator = ReflectField(host, "_orchestrator");
        var hub = ReflectField(runtime, "_hub");
        var hubState = hub is null ? "n/a" : ReflectProperty(hub, "State");
        var topUpHttp = WpfLiveServerTestHelper.LastTopUpStatus.HasValue
            ? WpfLiveServerTestHelper.LastTopUpStatus.Value.ToString(CultureInfo.InvariantCulture)
            : "n/a";
        var credentialHttp = WpfLiveServerTestHelper.LastCredentialStatus.HasValue
            ? WpfLiveServerTestHelper.LastCredentialStatus.Value.ToString(CultureInfo.InvariantCulture)
            : "n/a";

        return string.Format(
            CultureInfo.InvariantCulture,
            "Diagnostics: lastStatusText='{0}'; elapsedPollingSec={1:F1}; IsTransportConnected={2}; hubState={3}; runtimeState={4}; shellPhase={5}; hostSessionId={6}; runtimeSessionId={7}; walletTopUpHttp={8}; credentialPutHttp={9}",
            lastStatusText ?? "",
            elapsedPollingSec,
            ReflectProperty(runtime, "IsTransportConnected"),
            hubState,
            ReflectProperty(runtime, "State"),
            ReflectProperty(orchestrator, "Phase"),
            ReflectFieldText(host, "_sessionId"),
            ReflectFieldText(runtime, "_sessionId"),
            topUpHttp,
            credentialHttp);
    }

    private static object ReflectField(object target, string name)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(target);
        }
        catch
        {
            return null;
        }
    }

    private static string ReflectFieldText(object target, string name)
    {
        var value = ReflectField(target, name);
        return value?.ToString() ?? "n/a";
    }

    private static string ReflectProperty(object target, string name)
    {
        if (target is null)
        {
            return "n/a";
        }

        try
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var value = property?.GetValue(target);
            return value?.ToString() ?? "n/a";
        }
        catch
        {
            return "n/a";
        }
    }
}
#endif
