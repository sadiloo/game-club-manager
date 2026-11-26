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
using GameClub.Kiosk.Core.Setup;
using GameClub.Kiosk.Services;
using GameClub.Kiosk.Windows.IntegrationTests;
using GameClub.Kiosk.Windows.IntegrationTests.Server;
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
        using var isolated = WpfKioskSetupTestIsolation.CreateWithCompleteSetup();
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
    public void wpf_first_run_shows_setup_and_hides_lock_panel()
    {
        using var isolated = WpfKioskSetupTestIsolation.CreateWithoutSetup();
        Visibility lockVisible = Visibility.Visible;
        Visibility firstRunVisible = Visibility.Collapsed;
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
                    lockVisible = ((FrameworkElement)GetField(window, "LockPanel")).Visibility;
                    firstRunVisible = ((FrameworkElement)GetField(window, "FirstRunSetup")).Visibility;
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

        Assert.Equal(Visibility.Collapsed, lockVisible);
        Assert.Equal(Visibility.Visible, firstRunVisible);
    }

    [Fact]
    public void wpf_remaining_time_matches_funded_session_within_one_tick()
    {
        StabStepLog.Write("within_one_tick_begin");
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

        const long startWalletToman = 48_667;
        // R10k: the live server prices the wallet with its SEEDED pc-normal tariff (PilotSeed bands on an
        // installed server, TestHost harness window under kiosk integration CI) at the time of day the
        // session runs. The expected value comes from that seed data, not from a hard-coded day rate.
        var tariff = SeededPcBandTariff.ForLiveServerFromEnvironment();
        string capturedStatus = null;
        string lastPollStatusText = null;
        string pollFailureDiagnostics = null;
        Exception inProcessError = null;
        var loginStartedUtc = DateTime.MinValue;
        var capturedUtc = DateTime.MinValue;
        var at = GameClub.Billing.TehranTime.FromUtc(DateTime.UtcNow);
        var card = SeededTariffRateCard(tariff);
        var stabilityLogin = Environment.GetEnvironmentVariable("WPF_STABILITY_LOGIN") ?? "fund48667";
        var account = new PcAccount(stabilityLogin, startWalletToman);
        var estimatorSeconds = (int)TimeSpan.FromMinutes(
            RemainingTimeEstimator.Estimate(account, "pc-normal", PlayMode.Online, 1, at, card, graceMinuteEnabled: true).MoneyMinutes!.Value).TotalSeconds;
        Assert.Equal(tariff.ExpectedRemainingSeconds(startWalletToman, at.DateTime), estimatorSeconds);

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
                    loginStartedUtc = DateTime.UtcNow;
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
                            capturedUtc = DateTime.UtcNow;
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
        // The server builds the display at an instant between login and capture. Expected seconds are
        // taken at the session start (login). Only when a seeded band edge (08:30 / 00:00) lies in
        // (login, capture] may the display have been built at either rate (R10l); each candidate is exact ±1.
        var loginTehran = GameClub.Billing.TehranTime.FromUtc(loginStartedUtc).DateTime;
        var capturedTehran = GameClub.Billing.TehranTime.FromUtc(capturedUtc).DateTime;
        var expectedSeconds = tariff.ExpectedRemainingSeconds(startWalletToman, loginTehran);
        var expectedAtCapture = tariff.ExpectedRemainingSeconds(startWalletToman, capturedTehran);
        var edgeBetween = tariff.EdgeBetween(loginTehran, capturedTehran);
        var branch = tariff.RemainingTimeAssertBranch(startWalletToman, loginTehran, capturedTehran);
        var branchInfo =
            $"branch={branch} edge_tehran={(edgeBetween.HasValue ? edgeBetween.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "none")} "
            + $"login_tehran={loginTehran.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} captured_tehran={capturedTehran.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} "
            + $"expected_at_login={expectedSeconds} expected_at_capture={expectedAtCapture} displayed_seconds={displayedSeconds} "
            + tariff.Describe(startWalletToman, loginTehran);
        StabStepLog.Write("within_one_tick_expected", branchInfo);
        if (branch == SeededPcBandTariff.BranchEdgeWindow)
        {
            Assert.True(
                RemainingTimeOneTick.Within(expectedSeconds, displayedSeconds)
                || RemainingTimeOneTick.Within(expectedAtCapture, displayedSeconds),
                $"Band edge between login and capture: displayed {displayedSeconds}s must be within one tick of {expectedSeconds}s (login) or {expectedAtCapture}s (capture). {branchInfo}");
        }
        else
        {
            try
            {
                Assert.InRange(displayedSeconds, RemainingTimeOneTick.Low(expectedSeconds), RemainingTimeOneTick.High(expectedSeconds));
            }
            catch (Xunit.Sdk.InRangeException ex)
            {
                throw new Xunit.Sdk.XunitException($"{ex.Message} {branchInfo}");
            }
        }

        StabStepLog.Write("within_one_tick_end", $"displayed_seconds={displayedSeconds}");
    }

    [Fact]
    public void wpf_remaining_time_matches_funded_session_special_band_night_line()
    {
        var bandMode = Environment.GetEnvironmentVariable("CHECKPOINT_SERVER_PC_BAND")?.Trim();
        if (!string.Equals(bandMode, "night", StringComparison.OrdinalIgnoreCase))
        {
            IntegrationTestCi.FailIfActive("CHECKPOINT_SERVER_PC_BAND must be night in CI for wpf_remaining_time_matches_funded_session_special_band_night_line.");
            return;
        }

        var setupError = (Exception)null;
        var staSetup = new Thread(() =>
        {
            try
            {
                var loginName = "fund48667-night";
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

        const long startWalletToman = 48_667;
        var at = GameClub.Billing.TehranTime.FromUtc(DateTime.UtcNow);
        var tariff = SeededPcBandTariff.ForLiveServerFromEnvironment();
        Assert.True(tariff.InBand(at.DateTime.TimeOfDay), $"night line needs the harness band to cover now: {tariff.Describe(startWalletToman, at.DateTime)}");
        var expectedSeconds = tariff.ExpectedRemainingSeconds(startWalletToman, at.DateTime);
        var card = SeededTariffRateCard(tariff);
        var account = new PcAccount("fund48667-night", startWalletToman);
        var estimatorSeconds = (int)TimeSpan.FromMinutes(
            RemainingTimeEstimator.Estimate(account, "pc-normal", PlayMode.Online, 1, at, card, graceMinuteEnabled: true).MoneyMinutes!.Value).TotalSeconds;
        Assert.Equal(expectedSeconds, estimatorSeconds);

        string capturedStatus = null;
        string lastPollStatusText = null;
        string pollFailureDiagnostics = null;
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
                    var host = GetField(window, "_host");
                    await WpfLiveServerTransportHelper.WaitForKioskRuntimeTransportConnectedAsync(host, TimeSpan.FromSeconds(60));
                    var login = host.GetType().GetMethod("LoginAsync", BindingFlags.Instance | BindingFlags.Public);
                    await (Task)login.Invoke(host, new object[] { "fund48667-night", "pass" });
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

        var statusAssertMessage = "StatusText must show server-driven remaining time (night band).";
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
        Assert.InRange(displayedSeconds, RemainingTimeOneTick.Low(expectedSeconds), RemainingTimeOneTick.High(expectedSeconds));
    }

    private static RateCard SeededTariffRateCard(SeededPcBandTariff tariff) => new RateCardBuilder()
        .AddBase(PilotSeed.PcNormalGroupId, PlayMode.Online, null, tariff.BaseHourlyToman)
        .AddBand(new TimeBand(
            PilotSeed.PcNormalBandId,
            PilotSeed.PcNormalGroupId,
            "Seeded PC special hours " + tariff.Source,
            new GameClub.Billing.TimeOfDay(tariff.BandStart.Hours, tariff.BandStart.Minutes, tariff.BandStart.Seconds),
            new GameClub.Billing.TimeOfDay(tariff.BandEnd.Hours, tariff.BandEnd.Minutes, tariff.BandEnd.Seconds)))
        .AddBandRate(PilotSeed.PcNormalBandId, PilotSeed.PcNormalGroupId, PlayMode.Online, null, tariff.BandHourlyToman)
        .Build();

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
