#if NET48
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using GameClub.Shell;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using GameClub.Shell.Windows.IntegrationTests.Shell;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellPanelNavigationFailureTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellPanelNavigationFailureTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_probe_ok_navigation_503_shows_panel_unavailable_without_navigation_loop()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(503, 0);
                _fixture.RunAsync(async () =>
                {
                    await RunProbeOkNavigationFailureScenarioAsync(
                        stub,
                        expectSlowTimeout: false,
                        ciTraceTestName: nameof(shell_probe_ok_navigation_503_shows_panel_unavailable_without_navigation_loop));
                }, TimeSpan.FromSeconds(150));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_pending_panel_nav_past_deadline_confirms_once_late_completion_orphan_timed_out()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        ShellFileLog.Write("shell-deadline-orphan-baseline");
                        var logBaseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);

                        window = await StartWindowOnStubAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(90));
                        var navAtOffline = window.NavigationStartingCount;
                        Assert.Equal(0, window.PanelNavigationPendingCountForTests);

                        static string ReadShellLog() =>
                            Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                                .Select(File.ReadAllText)
                                .Aggregate("", string.Concat);

                        static string LogDelta(string baseline) =>
                            baseline.Length == 0 ? ReadShellLog() : ReadShellLog().Replace(baseline, "");

                        static bool IsPanelNavTimeoutConfirm(string line) =>
                            line.Contains("branch=panel-nav-timeout", StringComparison.Ordinal)
                            && !line.Contains("panel-nav-timeout-", StringComparison.Ordinal);

                        static bool TryParsePendingIdFromTimeoutConfirm(string line, out ulong pendingId)
                        {
                            pendingId = 0;
                            const string marker = "pendingId=";
                            var idx = line.IndexOf(marker, StringComparison.Ordinal);
                            if (idx < 0)
                            {
                                return false;
                            }

                            var start = idx + marker.Length;
                            var end = start;
                            while (end < line.Length && char.IsDigit(line[end]))
                            {
                                end++;
                            }

                            return end > start && ulong.TryParse(line.Substring(start, end - start), out pendingId);
                        }

                        var delta = LogDelta(logBaseline);
                        var timeoutConfirmLines = delta.Split('\n').Where(IsPanelNavTimeoutConfirm).ToArray();
                        Assert.Single(timeoutConfirmLines);
                        Assert.True(
                            TryParsePendingIdFromTimeoutConfirm(timeoutConfirmLines[0], out var timeoutNavId));

                        Assert.Equal(
                            1,
                            delta.Split('\n')
                                .Count(l => l.Contains("panel-navigate caller=PresentOffline", StringComparison.Ordinal)));
                        Assert.Equal(
                            0,
                            delta.Split('\n').Count(l =>
                                l.Contains("panel-nav-timeout-skip", StringComparison.Ordinal)
                                && l.Contains("reason=no-pending", StringComparison.Ordinal)));

                        string? matchingOrphan = null;
                        var orphanPollDeadline = DateTime.UtcNow.AddSeconds(40);
                        while (DateTime.UtcNow < orphanPollDeadline)
                        {
                            await Task.Delay(300);
                            delta = LogDelta(logBaseline);
                            matchingOrphan = delta.Split('\n').FirstOrDefault(l =>
                                l.Contains("nav-completed-orphan", StringComparison.Ordinal)
                                && l.Contains($"id={timeoutNavId}", StringComparison.Ordinal)
                                && l.Contains("timedOut=True", StringComparison.Ordinal));
                            if (matchingOrphan is not null)
                            {
                                break;
                            }
                        }

                        Assert.False(string.IsNullOrEmpty(matchingOrphan));

                        var steadyDeadline = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < steadyDeadline)
                        {
                            await Task.Delay(500);
                            Assert.Equal(navAtOffline, window.NavigationStartingCount);
                            Assert.Equal(0, window.PanelNavigationPendingCountForTests);
                        }

                        EchoR27dBarTracesToStdout(
                            nameof(shell_pending_panel_nav_past_deadline_confirms_once_late_completion_orphan_timed_out));
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_probe_ok_navigation_timeout_shows_panel_unavailable_without_navigation_loop()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
                {
                    await RunProbeOkNavigationFailureScenarioAsync(
                        stub,
                        expectSlowTimeout: true,
                        ciTraceTestName: nameof(shell_probe_ok_navigation_timeout_shows_panel_unavailable_without_navigation_loop));
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_probe_ok_navigation_failure_recovers_on_confirmed_2xx_without_extra_navigations()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(503, 0);
                _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = await StartWindowOnStubAsync(stub);
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(75));
                    var navAfterFail = window.NavigationStartingCount;
                    Assert.InRange(navAfterFail, 1, 3);

                    stub.OperatorStatusCode = 200;
                    stub.OperatorDelayMilliseconds = 0;
                    var recoverDeadline = DateTime.UtcNow.AddSeconds(90);
                    var panelOk = false;
                    while (DateTime.UtcNow < recoverDeadline)
                    {
                        await Task.Delay(500);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        var loc = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "location.pathname + '|' + document.readyState"));
                        if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                            && loc.EndsWith("|complete", StringComparison.Ordinal))
                        {
                            panelOk = true;
                            break;
                        }
                    }

                    Assert.True(panelOk, "panel did not recover on confirmed 2xx after probe-ok/navigation failure");
                    Assert.InRange(window.NavigationStartingCount, navAfterFail, navAfterFail + 1);
                    var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    var panelOkCount = log.Split('\n').Count(l => l.Contains("nav-completed-panel-ok", StringComparison.Ordinal));
                    Assert.InRange(panelOkCount, 1, 2);
                    EchoR27dBarTracesToStdout(
                        nameof(shell_probe_ok_navigation_failure_recovers_on_confirmed_2xx_without_extra_navigations));
                }
                finally
                {
                    RestoreStubSeams();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_stopped_in_flight_operator_nav_shows_offline_data_not_about_blank()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = await StartWindowOnStubAsync(stub);
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    var navStartedDeadline = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < navStartedDeadline && window.NavigationStartingCount < 1)
                    {
                        await Task.Delay(200);
                    }

                    Assert.True(window.NavigationStartingCount >= 1, "expected in-flight /operator/ navigation");
                    stub.OperatorStatusCode = 503;
                    stub.OperatorDelayMilliseconds = 0;

                    var offlineDeadline = DateTime.UtcNow.AddSeconds(90);
                    while (DateTime.UtcNow < offlineDeadline)
                    {
                        await Task.Delay(200);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        var href = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync("location.href"));
                        Assert.NotEqual("about:blank", href, StringComparer.OrdinalIgnoreCase);
                    }

                    wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                    Assert.NotNull(wv);
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(60));
                }
                finally
                {
                    RestoreStubSeams();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_after_stop_offline_data_nav_recovers_on_confirmed_2xx()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = await StartWindowOnStubAsync(stub);
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    var navStartedDeadline = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < navStartedDeadline && window.NavigationStartingCount < 1)
                    {
                        await Task.Delay(200);
                    }

                    stub.OperatorStatusCode = 503;
                    stub.OperatorDelayMilliseconds = 0;
                    wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                    Assert.NotNull(wv);
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(75));
                    var navAfterOffline = window.NavigationStartingCount;

                    stub.OperatorStatusCode = 200;
                    stub.OperatorDelayMilliseconds = 0;
                    var recoverDeadline = DateTime.UtcNow.AddSeconds(90);
                    var panelOk = false;
                    while (DateTime.UtcNow < recoverDeadline)
                    {
                        await Task.Delay(500);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        var loc = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "location.pathname + '|' + document.readyState"));
                        if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                            && loc.EndsWith("|complete", StringComparison.Ordinal))
                        {
                            panelOk = true;
                            break;
                        }
                    }

                    Assert.True(panelOk, "panel did not recover on confirmed 2xx after stop/offline data nav");
                    Assert.InRange(window.NavigationStartingCount, navAfterOffline, navAfterOffline + 1);
                    EchoR27dBarTracesToStdout(
                        nameof(shell_after_stop_offline_data_nav_recovers_on_confirmed_2xx));
                }
                finally
                {
                    RestoreStubSeams();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_load_offline_html_webview_recovery_exhausted_pins_title_body_and_hides_countdown()
    {
        var html = MainWindow.LoadOfflineHtmlForTests("webview-recovery-exhausted");
        var title = ExtractOfflineHtmlElementText(html, "h1", "title");
        var body = ExtractOfflineHtmlElementText(html, "p", "body");
        Assert.Equal("پنل اپراتور از کار افتاد", title);
        Assert.Contains("بازخوانی", body, StringComparison.Ordinal);
        Assert.Contains("id=\"countdown\" style=\"display:none\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<p id=\"countdown\"></p>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "سرور در دسترس نیست؛ دوباره تلاش می‌شود…",
            title,
            StringComparison.Ordinal);
    }

    [Fact]
    public void shell_probe_held_across_panel_nav_deadline_skips_stale_navigate_without_second_pending()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        var baseline = ReadShellLogSnapshot();
                        window = await StartWindowOnStubAsync(stub);
                        if (ShellSeams.HttpProbe is StubAlignedHttpProbeSeam alignedProbeSeam)
                        {
                            alignedProbeSeam.ProbeOkTimeoutOverrideMilliseconds = 120_000;
                        }

                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        ShellFileLog.Write("shell-probe-deadline-window-ready");

                        await AwaitShellLogLineAsync(
                            baseline,
                            line => line.Contains("branch=panel-nav-timeout-arm", StringComparison.Ordinal),
                            TimeSpan.FromSeconds(45));
                        Assert.True(
                            window.PanelNavigationPendingCountForTests >= 1,
                            "expected in-flight panel navigation before the 10s loopback deadline");

                        var probeEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        stub.ShellProbeHandlerEnteredForTests = probeEntered;
                        var probeRequestsBefore = stub.ProbeRequestCount;
                        stub.HoldNextShellProbeResponse();
                        window.TriggerProbeThenNavigatePanelForTests();
                        await AwaitTaskOrTimeoutAsync(
                            probeEntered.Task,
                            TimeSpan.FromSeconds(20),
                            "/shell-probe request did not start while held across deadline");
                        Assert.True(
                            stub.ProbeRequestCount > probeRequestsBefore,
                            "/shell-probe request did not start while held across deadline");

                        await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(45), baseline);
                        var deltaAfterTimeout = LogDelta(baseline);
                        var timeoutConfirm = deltaAfterTimeout.Split('\n').FirstOrDefault(line =>
                            line.Contains("branch=panel-nav-timeout", StringComparison.Ordinal)
                            && !line.Contains("panel-nav-timeout-", StringComparison.Ordinal));
                        Assert.False(string.IsNullOrEmpty(timeoutConfirm), "panel-nav-timeout was not confirmed");
                        Assert.Equal(0, window.PanelNavigationPendingCountForTests);

                        var navAfterDeadline = window.NavigationStartingCount;
                        stub.ReleaseShellProbeHold();

                        await AwaitShellLogLineAsync(
                            baseline,
                            line => line.Contains("reason=stale-probe-generation", StringComparison.Ordinal),
                            TimeSpan.FromSeconds(15));

                        Assert.Equal(navAfterDeadline, window.NavigationStartingCount);
                        Assert.Equal(0, window.PanelNavigationPendingCountForTests);
                        var deltaFinal = LogDelta(baseline);
                        var staleMark = deltaFinal.IndexOf("reason=stale-probe-generation", StringComparison.Ordinal);
                        Assert.True(staleMark >= 0);
                        var tailAfterStale = deltaFinal.Substring(staleMark);
                        Assert.DoesNotContain(
                            "panel-navigate caller=NavigatePanel",
                            tailAfterStale,
                            StringComparison.Ordinal);
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_fresh_probe_after_panel_nav_deadline_navigates_panel_once()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 25_000);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        window = await StartWindowOnStubAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(90));
                        var navAfterTimeoutOffline = window.NavigationStartingCount;
                        Assert.Equal(0, window.PanelNavigationPendingCountForTests);

                        stub.OperatorStatusCode = 200;
                        stub.OperatorDelayMilliseconds = 0;
                        stub.ProbeDelayMilliseconds = 0;
                        window.TriggerRunningPanelProbeForTests();

                        var panelDeadline = DateTime.UtcNow.AddSeconds(60);
                        var panelOk = false;
                        while (DateTime.UtcNow < panelDeadline)
                        {
                            await Task.Delay(300);
                            wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                            if (wv?.CoreWebView2 is null)
                            {
                                continue;
                            }

                            var loc = UnquoteWebViewScriptString(
                                await wv.CoreWebView2.ExecuteScriptAsync(
                                    "location.pathname + '|' + document.readyState"));
                            if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                                && loc.EndsWith("|complete", StringComparison.Ordinal))
                            {
                                panelOk = true;
                                break;
                            }
                        }

                        Assert.True(panelOk, "fresh probe after deadline did not navigate panel to operator");
                        Assert.Equal(navAfterTimeoutOffline + 1, window.NavigationStartingCount);
                        Assert.Equal(0, window.PanelNavigationPendingCountForTests);
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_panel_recovery_probe_logs_503_not_zero_with_production_http_seam()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(503, 0);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        ShellFileLog.Write("shell-probe-503-status-baseline");
                        var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);
                        window = await StartWindowOnStubWithProductionProbeAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(90));

                        var deadline = DateTime.UtcNow.AddSeconds(90);
                        string? recovery503Line = null;
                        while (DateTime.UtcNow < deadline)
                        {
                            await Task.Delay(300);
                            var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                                .Select(File.ReadAllText)
                                .Aggregate("", string.Concat);
                            var delta = baseline.Length == 0 ? log : log.Replace(baseline, "");
                            recovery503Line = delta.Split('\n').FirstOrDefault(l =>
                                l.Contains("panel-recovery-probe", StringComparison.Ordinal)
                                && l.Contains("status=503", StringComparison.Ordinal)
                                && l.Contains("outcome=fail", StringComparison.Ordinal));
                            if (recovery503Line is not null)
                            {
                                break;
                            }
                        }

                        Assert.False(string.IsNullOrEmpty(recovery503Line));
                        var finalDelta = LogDelta(baseline);
                        Assert.DoesNotContain(
                            "panel-recovery-probe target=panel status=0 outcome=fail",
                            finalDelta,
                            StringComparison.Ordinal);
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_recovery_probe_in_flight_reload_logs_stale_generation_without_navigate_panel()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(503, 0);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        ShellFileLog.Write("shell-stale-probe-baseline");
                        var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);
                        window = await StartWindowOnStubAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(90));
                        stub.OperatorStatusCode = 200;
                        stub.OperatorDelayMilliseconds = 0;
                        stub.ProbeDelayMilliseconds = 0;
                        stub.HoldNextPanelOperatorProbe();

                        var responsesBefore = stub.OperatorResponseCount;
                        window.TriggerRunningPanelProbeForTests();
                        var probeStarted = false;
                        var probeDeadline = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < probeDeadline)
                        {
                            await Task.Delay(50);
                            if (stub.OperatorResponseCount > responsesBefore)
                            {
                                probeStarted = true;
                                break;
                            }
                        }

                        Assert.True(probeStarted, "panel-recovery probe did not start before reload invalidation");
                        window.ReloadPanel();
                        stub.ReleasePanelOperatorProbeHold();

                        var staleLineDeadline = DateTime.UtcNow.AddSeconds(45);
                        var sawStale = false;
                        while (DateTime.UtcNow < staleLineDeadline)
                        {
                            await Task.Delay(200);
                            var delta = LogDelta(baseline);
                            if (delta.Contains("reason=stale-probe-generation", StringComparison.Ordinal))
                            {
                                sawStale = true;
                                break;
                            }
                        }

                        Assert.True(sawStale, "expected stale-probe-generation after reload during in-flight probe");
                        var afterDelta = LogDelta(baseline);
                        var staleMark = afterDelta.IndexOf(
                            "reason=stale-probe-generation",
                            StringComparison.Ordinal);
                        Assert.True(staleMark >= 0);
                        var tailAfterStale = afterDelta.Substring(staleMark);
                        Assert.DoesNotContain(
                            "panel-navigate caller=NavigatePanel",
                            tailAfterStale,
                            StringComparison.Ordinal);
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_exhausted_long_retry_stays_offline_when_panel_probe_returns_503()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 0);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        ShellFileLog.Write("shell-exhausted-503-baseline");
                        var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);
                        window = await StartWindowOnStubAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForOperatorPanelAsync(wv, TimeSpan.FromSeconds(45));
                        stub.OperatorStatusCode = 503;
                        stub.OperatorDelayMilliseconds = 0;
                        window.EnterProcessRecoveryExhaustedModeForTests();
                        wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForExhaustedOfflinePageAsync(wv, TimeSpan.FromSeconds(90));

                        var postExhausted = LogDelta(baseline);
                        var exhaustedMark = postExhausted.IndexOf(
                            "offlineState=webview-recovery-exhausted",
                            StringComparison.Ordinal);
                        Assert.True(exhaustedMark >= 0, "exhausted offline page was not presented");

                        var measureStart = DateTime.UtcNow;
                        var samples = new List<PanelOfflineFlashAssert.FlashSample>();
                        var measureDeadline = measureStart.AddSeconds(75);
                        var sawStayOffline = false;
                        while (DateTime.UtcNow < measureDeadline)
                        {
                            await Task.Delay(PanelOfflineFlashAssert.DefaultSampleIntervalMilliseconds);
                            wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                            var elapsedMs = (int)(DateTime.UtcNow - measureStart).TotalMilliseconds;
                            if (wv?.CoreWebView2 is null)
                            {
                                samples.Add(new PanelOfflineFlashAssert.FlashSample(
                                    PanelOfflineFlashAssert.SampleViewKind.NoWebView,
                                    elapsedMs));
                                continue;
                            }

                            var sampleJson = UnquoteWebViewScriptString(
                                await wv.CoreWebView2.ExecuteScriptAsync(
                                    "JSON.stringify({protocol: location.protocol, offline: document.documentElement.getAttribute('data-shell-offline-page')})"));
                            using var doc = JsonDocument.Parse(sampleJson);
                            var protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "";
                            var offlineAttr = doc.RootElement.GetProperty("offline").GetString();
                            var isOffline = protocol == "data:" && offlineAttr == "1";
                            samples.Add(new PanelOfflineFlashAssert.FlashSample(
                                isOffline
                                    ? PanelOfflineFlashAssert.SampleViewKind.Offline
                                    : PanelOfflineFlashAssert.SampleViewKind.NonOffline,
                                elapsedMs));

                            if (LogDelta(baseline).Contains(
                                    "action=stay-offline",
                                    StringComparison.Ordinal))
                            {
                                sawStayOffline = true;
                            }
                        }

                        Assert.True(sawStayOffline, "expected at least one exhausted stay-offline probe outcome");
                        var tailAfterMeasure = LogDelta(baseline).Substring(exhaustedMark);
                        Assert.DoesNotContain("caller=ExhaustedRecover", tailAfterMeasure, StringComparison.Ordinal);
                        Assert.DoesNotContain(
                            "outcome=exhausted-long-retry action=panel-navigate",
                            tailAfterMeasure,
                            StringComparison.Ordinal);
                        Assert.DoesNotContain(
                            "outcome=reload",
                            tailAfterMeasure,
                            StringComparison.Ordinal);
                        var flash = PanelOfflineFlashAssert.Evaluate(samples);
                        Assert.True(flash.Passes, $"exhausted 503 anti-flash: {flash.FailureReason}");
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(300));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_exhausted_long_retry_navigates_once_when_panel_probe_returns_2xx()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 0);
                _fixture.RunAsync(async () =>
                {
                    MainWindow? window = null;
                    try
                    {
                        ShellFileLog.Write("shell-exhausted-2xx-baseline");
                        var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);
                        window = await StartWindowOnStubAsync(stub);
                        var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForOperatorPanelAsync(wv, TimeSpan.FromSeconds(45));
                        stub.OperatorStatusCode = 503;
                        stub.OperatorDelayMilliseconds = 0;
                        window.EnterProcessRecoveryExhaustedModeForTests();
                        wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                        await WaitForExhaustedOfflinePageAsync(wv, TimeSpan.FromSeconds(90));

                        var pendingDeadline = DateTime.UtcNow.AddSeconds(15);
                        while (DateTime.UtcNow < pendingDeadline)
                        {
                            await Task.Delay(200);
                            if (LogDelta(baseline).Contains(
                                    "exhausted-long-retry delaySec=60 outcome=pending",
                                    StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        stub.OperatorStatusCode = 200;
                        stub.OperatorDelayMilliseconds = 0;
                        var recoverDeadline = DateTime.UtcNow.AddSeconds(75);
                        var panelOk = false;
                        while (DateTime.UtcNow < recoverDeadline)
                        {
                            await Task.Delay(300);
                            wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                            if (wv?.CoreWebView2 is null)
                            {
                                continue;
                            }

                            var loc = UnquoteWebViewScriptString(
                                await wv.CoreWebView2.ExecuteScriptAsync(
                                    "location.pathname + '|' + document.readyState"));
                            if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                                && loc.EndsWith("|complete", StringComparison.Ordinal))
                            {
                                panelOk = true;
                                break;
                            }
                        }

                        Assert.True(panelOk, "panel did not load after exhausted 2xx-gated navigation");
                        var delta = LogDelta(baseline);
                        Assert.Contains(
                            "action=panel-navigate",
                            delta,
                            StringComparison.Ordinal);
                        Assert.Equal(
                            1,
                            delta.Split('\n').Count(l =>
                                l.Contains("panel-navigate caller=ExhaustedRecover", StringComparison.Ordinal)));
                    }
                    finally
                    {
                        RestoreStubSeams();
                        window?.Close();
                    }
                }, TimeSpan.FromSeconds(300));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    [Fact]
    public void shell_tray_reloads_do_not_leak_webview_handler_wiring()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        ExecuteWithShellDataRoot(() =>
        {
            ShellPanelHttpSplitStub? stub = null;
            try
            {
                stub = CreateStub(200, 0);
                _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = await StartWindowOnStubAsync(stub);
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    var panelDeadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < panelDeadline)
                    {
                        await Task.Delay(300);
                        var loc = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "location.pathname + '|' + document.readyState"));
                        if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                            && loc.EndsWith("|complete", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    const int reloadCycles = 5;
                    var wireBefore = window.PanelWebViewWireCountForTests;
                    Assert.Equal(1, wireBefore);
                    var navBefore = window.NavigationStartingCount;
                    var handledBefore = window.NavigationCompletedHandledCountForTests;
                    for (var i = 0; i < reloadCycles; i++)
                    {
                        var targetNav = navBefore + i + 1;
                        var targetHandled = handledBefore + i + 1;
                        window.ReloadPanel();
                        var reloadDeadline = DateTime.UtcNow.AddSeconds(20);
                        while (DateTime.UtcNow < reloadDeadline
                               && (window.NavigationStartingCount < targetNav
                                   || window.NavigationCompletedHandledCountForTests < targetHandled))
                        {
                            await Task.Delay(200);
                        }

                        Assert.Equal(targetNav, window.NavigationStartingCount);
                        Assert.Equal(targetHandled, window.NavigationCompletedHandledCountForTests);
                        Assert.Equal(wireBefore, window.PanelWebViewWireCountForTests);
                    }
                }
                finally
                {
                    RestoreStubSeams();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(180));
            }
            finally
            {
                stub?.Dispose();
            }
        });
    }

    private void ExecuteWithShellDataRoot(Action testBody)
    {
        var root = AllocateShellDataRoot();
        var failed = false;
        try
        {
            testBody();
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            CleanupShellDataRoot(root, failed);
        }
    }

    private async Task RunProbeOkNavigationFailureScenarioAsync(
        ShellPanelHttpSplitStub stub,
        bool expectSlowTimeout,
        string? ciTraceTestName = null)
    {
        MainWindow? window = null;
        try
        {
            window = await StartWindowOnStubAsync(stub);
            var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
            await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(expectSlowTimeout ? 90 : 75));
            var navCount = window.NavigationStartingCount;
            Assert.InRange(navCount, 1, 3);

            var measureDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < measureDeadline)
            {
                await Task.Delay(500);
            }

            Assert.Equal(navCount, window.NavigationStartingCount);
            Assert.Equal(0, window.PanelNavigationPendingCountForTests);
            if (expectSlowTimeout)
            {
                Assert.True(
                    stub.ProbeSuccessCount >= 1,
                    "Expected at least one /shell-probe/ 200 while /operator/ was slow");
                Assert.True(
                    stub.WasProbeOkServedDuringOperatorDelay,
                    "Expected /shell-probe/ 200 during an in-flight /operator/ delay window");
            }

            var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
            var presentOfflineCount = log.Split('\n')
                .Count(l => l.Contains("panel-navigate caller=PresentOffline", StringComparison.Ordinal));
            Assert.InRange(presentOfflineCount, 1, 2);
            Assert.InRange(
                log.Split('\n').Count(l => l.Contains("panel-nav-retry", StringComparison.Ordinal)),
                1,
                5);
            if (ciTraceTestName is not null)
            {
                EchoR27dBarTracesToStdout(ciTraceTestName);
            }
        }
        finally
        {
            RestoreStubSeams();
            window?.Close();
        }
    }

    private static ShellPanelHttpSplitStub CreateStub(int operatorStatusCode, int operatorDelayMs)
    {
        var stub = new ShellPanelHttpSplitStub(ShellPanelHttpSplitStub.AllocateLoopbackPort())
        {
            OperatorStatusCode = operatorStatusCode,
            OperatorDelayMilliseconds = operatorDelayMs,
        };
        stub.Start();
        return stub;
    }

    private static async Task<MainWindow> StartWindowOnStubAsync(ShellPanelHttpSplitStub stub)
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        ShellSeams.HttpProbe = new StubAlignedHttpProbeSeam(stub.ProbeUrl);
        ShellSeams.PanelOriginOverride = stub.PanelOrigin;
        ShellSeams.PanelNavigationUrlOverride = stub.OperatorUrl;
        ShellSeams.HttpProbeUrlOverride = stub.ProbeUrl;

        var window = new MainWindow();
        window.Show();
        await Task.Delay(1);
        return window;
    }

    private static async Task<MainWindow> StartWindowOnStubWithProductionProbeAsync(ShellPanelHttpSplitStub stub)
    {
        FakeShellSeams.Install();
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        FakeShellSeams.Http.Answers = true;
        ShellSeams.HttpProbe = new ProductionHttpProbeSeam();
        ShellSeams.PanelOriginOverride = stub.PanelOrigin;
        ShellSeams.PanelNavigationUrlOverride = stub.OperatorUrl;
        ShellSeams.HttpProbeUrlOverride = stub.ProbeUrl;

        var window = new MainWindow();
        window.Show();
        await Task.Delay(1);
        return window;
    }

    private static string ReadShellLogSnapshot()
    {
        var paths = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log");
        var builder = new System.Text.StringBuilder();
        foreach (var path in paths)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            builder.Append(reader.ReadToEnd());
        }

        return builder.ToString();
    }

    private static string LogDelta(string baseline) =>
        baseline.Length == 0
            ? ReadShellLogSnapshot()
            : ReadShellLogSnapshot().Replace(baseline, "");

    private static async Task AwaitTaskOrTimeoutAsync(Task task, TimeSpan timeout, string timeoutMessage)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != task)
        {
            throw new System.TimeoutException(timeoutMessage);
        }

        await task.ConfigureAwait(false);
    }

    private static async Task<string> AwaitShellLogLineAsync(
        string baseline,
        Func<string, bool> linePredicate,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(timeout);
        timeoutCts.Token.Register(() =>
            tcs.TrySetException(new System.TimeoutException("expected shell log line was not written")));

        void TryMatch(object? _)
        {
            if (tcs.Task.IsCompleted)
            {
                return;
            }

            try
            {
                foreach (var line in LogDelta(baseline).Split('\n'))
                {
                    if (linePredicate(line))
                    {
                        tcs.TrySetResult(line);
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        using var pollTimer = new Timer(TryMatch, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        TryMatch(null);
        return await tcs.Task.ConfigureAwait(false);
    }

    private static async Task WaitForOperatorPanelAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 wv,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var loc = UnquoteWebViewScriptString(
                await wv.CoreWebView2.ExecuteScriptAsync(
                    "location.pathname + '|' + document.readyState"));
            if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                && loc.EndsWith("|complete", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException("Operator panel did not load on stub");
    }

    private static async Task WaitForExhaustedOfflinePageAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 wv,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var sample = UnquoteWebViewScriptString(
                await wv.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({protocol: location.protocol, offline: document.documentElement.getAttribute('data-shell-offline-page'), title: (document.getElementById('title') || {}).innerText || ''})"));
            using var doc = JsonDocument.Parse(sample);
            var protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "";
            var offlineAttr = doc.RootElement.GetProperty("offline").GetString();
            var title = doc.RootElement.GetProperty("title").GetString() ?? "";
            if (protocol == "data:" && offlineAttr == "1"
                && title.Contains("پنل اپراتور از کار افتاد", StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException("Exhausted offline page did not appear");
    }

    private static void RestoreStubSeams()
    {
        FakeShellSeams.RestoreProduction();
    }

    private static async Task<Microsoft.Web.WebView2.Wpf.WebView2> WaitForPanelWebViewAsync(
        MainWindow window,
        TimeSpan timeout)
    {
        Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
            if (wv?.CoreWebView2 is not null)
            {
                return wv;
            }
        }

        throw new InvalidOperationException("Panel WebView2 did not become ready");
    }

    private static bool LogLineIsPanelUnavailableOfflineReady(string line)
    {
        if (!line.Contains("offlineState=panel-unavailable", StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Contains("branch=nav-completed-offline-data", StringComparison.Ordinal)
            && line.Contains("offlineShown=True", StringComparison.Ordinal)
            && line.Contains("IsSuccess=True", StringComparison.Ordinal))
        {
            return true;
        }

        return line.Contains("branch=present-offline-navigated", StringComparison.Ordinal);
    }

    private static bool LogTextContainsPanelUnavailableOfflineReady(string text) =>
        text.Split('\n').Any(LogLineIsPanelUnavailableOfflineReady);

    private static async Task WaitForPanelUnavailableOfflineAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 wv,
        TimeSpan timeout,
        string? logBaseline = null)
    {
        try
        {
            await AssertPanelUnavailableOfflineDomAsync(wv, TimeSpan.FromSeconds(1));
            return;
        }
        catch (InvalidOperationException)
        {
        }

        if (logBaseline is not null)
        {
            if (!LogTextContainsPanelUnavailableOfflineReady(LogDelta(logBaseline)))
            {
                await AwaitShellLogLineAsync(logBaseline, LogLineIsPanelUnavailableOfflineReady, timeout);
            }
        }
        else
        {
            var snapshot = ReadShellLogSnapshot();
            if (!LogTextContainsPanelUnavailableOfflineReady(snapshot))
            {
                await AwaitShellLogLineAsync(snapshot, LogLineIsPanelUnavailableOfflineReady, timeout);
            }
        }

        await AssertPanelUnavailableOfflineDomAsync(wv, TimeSpan.FromSeconds(10));
    }

    private static async Task AssertPanelUnavailableOfflineDomAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 wv,
        TimeSpan confirmTimeout)
    {
        var deadline = DateTime.UtcNow.Add(confirmTimeout);
        while (DateTime.UtcNow < deadline)
        {
            var sample = UnquoteWebViewScriptString(
                await wv.CoreWebView2.ExecuteScriptAsync(
                    "JSON.stringify({protocol: location.protocol, offline: document.documentElement.getAttribute('data-shell-offline-page'), title: (document.getElementById('title') || {}).innerText || ''})"));
            if (string.IsNullOrWhiteSpace(sample))
            {
                await Task.Delay(50);
                continue;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(sample);
            }
            catch (JsonException)
            {
                await Task.Delay(50);
                continue;
            }

            using (doc)
            {
                if (doc.RootElement.GetProperty("protocol").GetString() == "data:"
                    && doc.RootElement.GetProperty("offline").GetString() == "1"
                    && (doc.RootElement.GetProperty("title").GetString() ?? "")
                        .Contains("پنل در دسترس نیست", StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(50);
        }

        throw new InvalidOperationException("panel-unavailable offline page did not appear");
    }

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

    private static string AllocateShellDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "gcm-shell-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "WebView2"));
        return root;
    }

    private static void CleanupShellDataRoot(string root, bool testFailed)
    {
        if (testFailed)
        {
            DumpShellDataRootOnFailure(root);
        }

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

    private static void DumpShellDataRootOnFailure(string root)
    {
        var logsDir = Path.Combine(root, "logs");
        if (!Directory.Exists(logsDir))
        {
            return;
        }

        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            var artifactLogs = Path.Combine(workspace, "artifacts", "shell-windows-logs");
            var dest = Path.Combine(artifactLogs, Path.GetFileName(root));
            try
            {
                Directory.CreateDirectory(dest);
                foreach (var logPath in Directory.GetFiles(logsDir, "shell-*.log"))
                {
                    File.Copy(logPath, Path.Combine(dest, Path.GetFileName(logPath)), overwrite: true);
                }
            }
            catch
            {
            }
        }

        foreach (var logPath in Directory.GetFiles(logsDir, "shell-*.log"))
        {
            foreach (var line in File.ReadAllLines(logPath))
            {
                if (IsR27dCiTraceLine(line)
                    || line.Contains("SHELL-RACE-NAV", StringComparison.Ordinal)
                    || line.Contains("panel-navigate", StringComparison.Ordinal))
                {
                    Console.WriteLine($"SHELL-TRACE-DUMP root={Path.GetFileName(root)} {line}");
                }
            }
        }
    }

    private static bool IsR27dCiTraceLine(string line)
    {
        if (line.Contains("panel-recovery-probe", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("panel-navigate caller=NavigatePanel", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("nav-completed-panel-ok", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("nav-completed-orphan", StringComparison.Ordinal)
            && line.Contains("timedOut=True", StringComparison.Ordinal))
        {
            return true;
        }

        if (!line.Contains("SHELL-OFFLINE-TRACE", StringComparison.Ordinal))
        {
            return false;
        }

        if (line.Contains("branch=nav-starting", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("branch=panel-nav-timeout", StringComparison.Ordinal)
            && !line.Contains("panel-nav-timeout-", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.Contains("branch=nav-completed-orphan", StringComparison.Ordinal)
            && line.Contains("timedOut=True", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static void EchoR27dBarTracesToStdout(string testName)
    {
        if (!Directory.Exists(ShellPaths.ShellLogsDir))
        {
            return;
        }

        foreach (var logPath in Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log"))
        {
            foreach (var line in File.ReadAllLines(logPath))
            {
                if (!IsR27dCiTraceLine(line))
                {
                    continue;
                }

                Console.WriteLine($"SHELL-R27D-CI-TRACE test={testName} {line}");
            }
        }
    }

    private static string ExtractOfflineHtmlElementText(string html, string tag, string id)
    {
        var pattern = $@"<{tag} id=""{id}"">([^<]*)</{tag}>";
        var match = System.Text.RegularExpressions.Regex.Match(html, pattern);
        Assert.True(match.Success, $"expected <{tag} id=\"{id}\"> in offline html");
        return match.Groups[1].Value;
    }

    private static string UnquoteWebViewScriptString(string jsonQuoted)
    {
        if (string.IsNullOrEmpty(jsonQuoted))
        {
            return "";
        }

        if (jsonQuoted.Length >= 2 && jsonQuoted[0] == '"' && jsonQuoted[jsonQuoted.Length - 1] == '"')
        {
            return JsonSerializer.Deserialize<string>(jsonQuoted) ?? "";
        }

        return jsonQuoted;
    }
}
#endif