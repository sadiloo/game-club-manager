#if NET48
using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
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

    private static async Task WaitForPanelUnavailableOfflineAsync(
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
            if (doc.RootElement.GetProperty("protocol").GetString() == "data:"
                && doc.RootElement.GetProperty("offline").GetString() == "1"
                && (doc.RootElement.GetProperty("title").GetString() ?? "")
                    .Contains("پنل در دسترس نیست", StringComparison.Ordinal))
            {
                return;
            }
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