#if NET48
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.ServiceProcess;
using System.Text.Json;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using GameClub.Shell;
using GameClub.Shell.ServerControl;
using GameClub.Shell.Shell;
using GameClub.Shell.Windows.IntegrationTests.Seams;
using GameClub.Shell.Windows.IntegrationTests.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Xunit;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

[Collection("windows-shell-sta")]
[Trait("Category", "WindowsShell")]
public sealed class ShellPlanAIntegrationTests
{
    private readonly WpfStaApplicationFixture _fixture;

    public ShellPlanAIntegrationTests(WpfStaApplicationFixture fixture) => _fixture = fixture;

    [Fact]
    public void shell_first_run_enables_checkpoint_shell_run_value()
    {
        FakeShellSeams.RestoreProduction();
        var backup = BackupAutostartValue();
        try
        {
            ClearAutostartValue();
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                await Task.Delay(1500);
                Assert.True(AutostartRegistry.IsEnabled);
                window.Close();
            }, TimeSpan.FromSeconds(20));
        }
        finally
        {
            RestoreAutostartValue(backup);
        }
    }

    [Fact]
    public void shell_autostart_uncheck_removes_run_value()
    {
        var backup = BackupAutostartValue();
        try
        {
            AutostartRegistry.SetEnabled(true);
            Assert.True(AutostartRegistry.IsEnabled);
            AutostartRegistry.SetEnabled(false);
            Assert.False(AutostartRegistry.IsEnabled);
        }
        finally
        {
            RestoreAutostartValue(backup);
        }
    }

    [Fact]
    public void shell_checkpoint_server_service_is_delayed_auto_start()
    {
        var psi = new ProcessStartInfo("sc.exe", "qc CheckpointServer")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        Assert.Contains("DELAYED", output.ToUpperInvariant());
    }

    [Fact]
    public void shell_window_json_restores_size_position_maximized()
    {
        var root = AllocateShellDataRoot();
        try
        {
            var placement = new WindowPlacement
            {
                Left = 120,
                Top = 80,
                Width = 1024,
                Height = 700,
                IsMaximized = true,
            };
            Directory.CreateDirectory(root);
            File.WriteAllText(ShellPaths.WindowJsonPath, JsonSerializer.Serialize(placement));

            _fixture.RunAsync(() =>
            {
                var window = new MainWindow();
                var loadedPlacement = new TaskCompletionSource<bool>();
                WriteShellGeom("placement-restore", "PreShow", window);
                window.SourceInitialized += (_, _) =>
                    WriteShellGeom("placement-restore", "SourceInitialized-after-ApplyBounds", window);
                window.Loaded += (_, _) =>
                {
                    WriteShellGeom("placement-restore", "Loaded", window);
                    WriteGeometryTimeline("placement-restore", "Loaded", window);
                    try
                    {
                        ShellGeomLoadedWait.RunLoadedAsserts(
                            window,
                            "placement-restore",
                            () =>
                            {
                                Assert.Equal(WindowState.Maximized, window.WindowState);
                                // Maximized frame size includes borders; normal restore bounds follow WindowPlacementStore clamp.
                                var area = Screen.PrimaryScreen!.WorkingArea;
                                Assert.Equal(Math.Min(1024, area.Width), window.RestoreBounds.Width, 0);
                                Assert.Equal(Math.Min(700, area.Height), window.RestoreBounds.Height, 0);
                            });
                        loadedPlacement.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        loadedPlacement.TrySetException(ex);
                    }
                };
                window.ContentRendered += (_, _) =>
                    WriteGeometryTimeline("placement-restore", "ContentRendered", window);
                window.Show();
                WriteShellGeom("placement-restore", "Show", window);
                WriteGeometryTimeline("placement-restore", "Show", window);
                ShellGeomLoadedWait.AwaitLoaded(loadedPlacement, "placement-restore", TimeSpan.FromSeconds(5));

                window.Close();
                return Task.CompletedTask;
            }, TimeSpan.FromSeconds(15));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_window_json_clamps_position_when_monitor_missing()
    {
        var root = AllocateShellDataRoot();
        try
        {
            var (left, top) = WindowPlacementStore.ClampPositionForTests(-8000, -8000, 1280, 800);
            Assert.True(left > -1000);
            Assert.True(top > -1000);
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_close_hide_exit_and_kill_leave_checkpoint_server_running()
    {
        FakeShellSeams.RestoreProduction();
        EnsureServiceRunning();
        _fixture.RunAsync(async () =>
        {
            var window = new MainWindow();
            window.Show();
            window.Hide();
            await Task.Delay(300);
            Assert.True(ServiceIsRunning());
            window.Close();
            await Task.Delay(300);
            Assert.True(ServiceIsRunning());
        }, TimeSpan.FromSeconds(25));

        var shellProc = LaunchShellProcess();
        try
        {
            Assert.True(ServiceIsRunning());
            shellProc.Refresh();
            Assert.False(shellProc.HasExited);

            shellProc.Kill();
            shellProc.WaitForExit(15_000);
            Assert.True(ServiceIsRunning());
            Assert.True(ShellCiHttp.TryGetStationsOk());
        }
        finally
        {
            if (!shellProc.HasExited) shellProc.Kill();
            shellProc.Dispose();
        }
    }

    [Fact]
    public void shell_events_do_not_change_open_session_amount_after_ten_minutes()
    {
        FakeShellSeams.RestoreProduction();
        EnsureServiceRunning();
        var shellStation = "SHELL-A3-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        var refStation = "SHELL-REF-" + Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant();
        var shellHandle = ShellCiHttp.CreateOpenSessionAsync(shellStation).GetAwaiter().GetResult();
        var refHandle = ShellCiHttp.CreateOpenSessionAsync(refStation).GetAwaiter().GetResult();

        _fixture.RunAsync(async () =>
        {
            var window = new MainWindow();
            window.Show();
            window.Hide();
            window.ShowFromTray();
            window.ReloadPanel();
            await Task.Delay(500);
            window.Close();
            await Task.Delay(300);
        }, TimeSpan.FromSeconds(40));

        var shellProc = LaunchShellProcess();
        try
        {
            Thread.Sleep(2000);
            shellProc.Kill();
            shellProc.WaitForExit(10_000);
        }
        finally
        {
            shellProc.Dispose();
        }

        var waitSeconds = ResolveBillingWaitSeconds();
        Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));

        Assert.Equal("Playing", ShellCiHttp.GetOperatorGridStatusForStationAsync(refStation).GetAwaiter().GetResult());
        Assert.Equal("Playing", ShellCiHttp.GetOperatorGridStatusForStationAsync(shellStation).GetAwaiter().GetResult());

        var refStartUtc = ShellCiHttp.GetFirstSegmentStartUtcFromServerAsync(refHandle.SessionId).GetAwaiter().GetResult();
        var shellStartUtc = ShellCiHttp.GetFirstSegmentStartUtcFromServerAsync(shellHandle.SessionId).GetAwaiter().GetResult();
        ShellCiHttp.WaitUntilBothSessionsClearMinuteBoundary(
            refStartUtc,
            shellStartUtc,
            TimeSpan.FromMinutes(2));

        var refBill = ShellCiHttp.OpenFrozenBillAsync(refHandle.SessionId).GetAwaiter().GetResult();
        var shellBill = ShellCiHttp.OpenFrozenBillAsync(shellHandle.SessionId).GetAwaiter().GetResult();

        var refElapsed = ShellCiHttp.WholeElapsedMinutes(refBill.FirstSegmentStartUtc, refBill.FrozenAtUtc);
        var shellElapsed = ShellCiHttp.WholeElapsedMinutes(shellBill.FirstSegmentStartUtc, shellBill.FrozenAtUtc);
        var minWholeMinutes = waitSeconds / 60;
        if (refElapsed < minWholeMinutes || shellElapsed < minWholeMinutes)
        {
            throw new InvalidOperationException(
                $"SHELL-MONEY elapsed whole minutes below billing wait floor ({minWholeMinutes} from {waitSeconds}s): "
                + $"REF={refElapsed} A3={shellElapsed}; "
                + $"REF start={refBill.FirstSegmentStartUtc:O} frozen={refBill.FrozenAtUtc:O}; "
                + $"A3 start={shellBill.FirstSegmentStartUtc:O} frozen={shellBill.FrozenAtUtc:O}");
        }

        const long minimumAccruedTimeGrossToman = 2667L;
        if (refBill.TimeGrossToman <= minimumAccruedTimeGrossToman
            || shellBill.TimeGrossToman <= minimumAccruedTimeGrossToman)
        {
            throw new InvalidOperationException(
                $"SHELL-MONEY timeGrossToman at or below minimum-charge floor ({minimumAccruedTimeGrossToman}): "
                + $"REF elapsed={refElapsed} timeGross={refBill.TimeGrossToman} collectible={refBill.CollectibleToman}; "
                + $"A3 elapsed={shellElapsed} timeGross={shellBill.TimeGrossToman} collectible={shellBill.CollectibleToman}; "
                + $"REF start={refBill.FirstSegmentStartUtc:O} frozen={refBill.FrozenAtUtc:O}; "
                + $"A3 start={shellBill.FirstSegmentStartUtc:O} frozen={shellBill.FrozenAtUtc:O}");
        }

        if (refElapsed != shellElapsed)
        {
            throw new InvalidOperationException(
                $"SHELL-MONEY elapsed whole minutes differ: REF={refElapsed} A3={shellElapsed}; "
                + $"REF start={refBill.FirstSegmentStartUtc:O} frozen={refBill.FrozenAtUtc:O}; "
                + $"A3 start={shellBill.FirstSegmentStartUtc:O} frozen={shellBill.FrozenAtUtc:O}");
        }

        ShellCiHttp.LogShellMoneyLine("REF", refBill);
        ShellCiHttp.LogShellMoneyLine("A3", shellBill);

        Assert.Equal(refBill.CollectibleToman, shellBill.CollectibleToman);
        Assert.Equal(refBill.TimeGrossToman, shellBill.TimeGrossToman);
        Assert.Equal(refBill.SegmentCount, shellBill.SegmentCount);
        AssertFrozenBillSegmentsMatch(refBill, shellBill);
    }

    [Fact]
    public void shell_elevated_stop_with_zero_sessions_skips_warning()
    {
        try
        {
            FakeShellSeams.Install();
            ShellSeams.ReadOpenSessionCount = () => 0;
            ShellSeams.MessageBoxHandler = _ => throw new InvalidOperationException("dialog should not appear");
            FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
            var code = ServerControlProgram.Run("stop");
            Assert.InRange(code, 0, 4);
        }
        finally
        {
            FakeShellSeams.RestoreProduction();
        }
    }

    [Fact]
    public void shell_elevated_stop_shows_open_session_count_warning()
    {
        try
        {
            FakeShellSeams.Install();
            ShellSeams.ReadOpenSessionCount = () => 2;
            ShellSeams.MessageBoxHandler = req =>
            {
                Assert.Contains("۲", req.Text);
                return System.Windows.Forms.DialogResult.Cancel;
            };
            FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
            var code = ServerControlProgram.Run("stop");
            Assert.Equal(1, code);
            Assert.NotNull(ShellSeams.LastMessageBoxRequest);
        }
        finally
        {
            FakeShellSeams.RestoreProduction();
        }
    }

    [Fact]
    public void shell_elevated_stop_cancel_leaves_service_running()
    {
        try
        {
            FakeShellSeams.Install();
            ShellSeams.ReadOpenSessionCount = () => 3;
            ShellSeams.MessageBoxHandler = _ => System.Windows.Forms.DialogResult.Cancel;
            FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
            var code = ServerControlProgram.Run("stop");
            Assert.Equal(1, code);
            Assert.Equal(ServiceControllerStatus.Running, FakeShellSeams.Service.Status);
        }
        finally
        {
            FakeShellSeams.RestoreProduction();
        }
    }

    [Fact]
    public void shell_tray_start_brings_service_to_running()
    {
        FakeShellSeams.RestoreProduction();
        EnsureServiceRunning();
        using var sc = new ServiceController(ShellPaths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Running)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }

        var code = ServerControlProgram.Run("start");
        Assert.Equal(0, code);
        sc.Refresh();
        Assert.Equal(ServiceControllerStatus.Running, sc.Status);
    }

    [Fact]
    public void shell_standard_user_cannot_stop_service()
    {
        var user = Environment.GetEnvironmentVariable("GCM_SHELL_STD_USER");
        var password = Environment.GetEnvironmentVariable("GCM_SHELL_STD_PASSWORD");
        Assert.False(string.IsNullOrWhiteSpace(user));
        Assert.False(string.IsNullOrWhiteSpace(password));

        EnsureServiceRunning();
        var exe = ResolveShellExe();
        var secure = new SecureString();
        foreach (var ch in password!)
        {
            secure.AppendChar(ch);
        }

        var psi = new ProcessStartInfo(exe, "--server-control stop")
        {
            UserName = user,
            Password = secure,
            UseShellExecute = false,
            LoadUserProfile = true,
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit(60_000);
        Assert.NotEqual(0, proc.ExitCode);
        Assert.True(ServiceIsRunning());
    }

    [Fact]
    public void shell_shows_persian_offline_when_service_stopped()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        StopServiceIfRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    var html = "";
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(300);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        html = await wv.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                        if (UnquoteWebViewScriptString(html).Contains("سرور", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    Assert.Contains("سرور", html);
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
            EnsureServiceRunning();
        }
    }

    [Fact]
    public void shell_shows_persian_offline_after_service_stops_post_webview_ready()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    var webViewDeadline = DateTime.UtcNow.AddSeconds(30);
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    while (DateTime.UtcNow < webViewDeadline)
                    {
                        await Task.Delay(300);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is not null)
                        {
                            break;
                        }
                    }

                    using (var sc = new ServiceController("CheckpointServer"))
                    {
                        if (sc.Status != ServiceControllerStatus.Stopped)
                        {
                            sc.Stop();
                            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
                        }
                    }

                    var html = "";
                    var textDeadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < textDeadline)
                    {
                        await Task.Delay(300);
                        var serviceStatus = ReadCheckpointServiceStatus();
                        Console.WriteLine(
                            $"SHELL-OFFLINE poll UTC={DateTime.UtcNow:O} service={serviceStatus} webViewReady={wv?.CoreWebView2 is not null} uri={wv?.Source}");
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        html = await wv.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                        if (UnquoteWebViewScriptString(html).Contains("سرور", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    Assert.Contains("سرور", html);
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(90));
        }
        finally
        {
            CleanupShellDataRoot(root);
            EnsureServiceRunning();
        }
    }

    [Fact]
    public void shell_navigates_to_operator_within_ten_seconds_after_service_start()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(300);
                    if (wv?.Source is not null && wv.Source.ToString().Contains("/operator/", StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                }

                Assert.Contains("/operator/", wv?.Source?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                window.Close();
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_blocks_external_navigation_and_new_window_with_log_line()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        var logDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logDir);
        try
        {
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                await Task.Delay(4000);
                var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                Assert.NotNull(wv?.CoreWebView2);
                ShellFileLog.Write("shell-integration-baseline");
                var before = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                wv.CoreWebView2.Navigate("https://example.com/");
                await Task.Delay(1000);
                var after = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                Assert.Contains("navigation-blocked", after);
                Assert.DoesNotContain("example.com", after.Replace(before, ""));
                window.Close();
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_panel_navigation_origin_blocks_spoofed_loopback_urls()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                await Task.Delay(4000);
                var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                Assert.NotNull(wv?.CoreWebView2);
                var logCursor = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                var spoofed = new (string Uri, string ExpectedHost)[]
                {
                    ("http://127.0.0.1:50001/operator/", "127.0.0.1"),
                    ("https://127.0.0.1:5000/operator/", "127.0.0.1"),
                    ("http://127.0.0.1.evil.test:5000/operator/", "127.0.0.1.evil.test"),
                    ("http://127.0.0.1:5000@evil.com/operator/", "evil.com"),
                    ("http://localhost:5000/operator/", "localhost"),
                };
                var handlerBlockedCount = 0;
                foreach (var (uri, expectedHost) in spoofed)
                {
                    var beforeAttempt = logCursor;
                    var navigateRejected = false;
                    try
                    {
                        wv.CoreWebView2.Navigate(uri);
                    }
                    catch (ArgumentException)
                    {
                        navigateRejected = true;
                    }

                    var blockedLineDeadline = DateTime.UtcNow.AddSeconds(5);
                    var hash8 = MainWindow.HostHash8ForTests(expectedHost);
                    var blockedLine = "";
                    while (DateTime.UtcNow < blockedLineDeadline)
                    {
                        await Task.Delay(50);
                        logCursor = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                        var attemptDelta = logCursor.Replace(beforeAttempt, "");
                        blockedLine = attemptDelta
                            .Split('\n')
                            .LastOrDefault(l => l.Contains("navigation-blocked", StringComparison.Ordinal) && l.Contains($"host_hash8={hash8}", StringComparison.Ordinal))
                            ?? "";
                        if (!string.IsNullOrEmpty(blockedLine))
                        {
                            handlerBlockedCount++;
                            break;
                        }
                    }

                    Assert.True(
                        navigateRejected || !string.IsNullOrEmpty(blockedLine),
                        $"Expected Navigate rejection or navigation-blocked log with host_hash8={hash8} for {uri}");
                    if (!navigateRejected)
                    {
                        await Task.Delay(300);
                        var href = UnquoteWebViewScriptString(await wv.CoreWebView2.ExecuteScriptAsync("location.href"));
                        Assert.DoesNotContain("evil.com", href, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain(":50001", href, StringComparison.Ordinal);
                        Assert.DoesNotContain("127.0.0.1.evil.test", href, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("localhost:5000", href, StringComparison.OrdinalIgnoreCase);
                        Assert.False(
                            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                            $"WebView committed blocked URI {uri}");
                    }
                }

                Assert.True(handlerBlockedCount >= 1, "Expected at least one spoofed URI blocked by NavigationStarting handler");

                await window.NavigatePanelAsync();
                await Task.Delay(1000);
                Assert.Contains("/operator/", wv.Source?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                window.Close();
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_offline_data_navigation_permission_cleared_after_interrupted_load()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    var readyDeadline = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < readyDeadline)
                    {
                        await Task.Delay(100);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is not null)
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    var offlineNavStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnNavStarting(object? _, CoreWebView2NavigationStartingEventArgs args)
                    {
                        if (!args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        offlineNavStarted.TrySetResult(true);
                        wv!.CoreWebView2.Stop();
                    }

                    wv.CoreWebView2.NavigationStarting += OnNavStarting;
                    window.NavigateToStringWithOfflineDataPermissionForTests(
                        "<html><body>ARMED-OFFLINE-INTERRUPT</body></html>");
                    var sawOfflineNav = await Task.WhenAny(offlineNavStarted.Task, Task.Delay(5000)) == offlineNavStarted.Task;
                    Assert.True(sawOfflineNav, "Expected data: NavigationStarting for armed offline navigation");
                    await Task.Delay(500);
                    wv.CoreWebView2.NavigationStarting -= OnNavStarting;
                    Assert.False(
                        window.OfflineDataNavigationPermissionArmedForTests,
                        "Permission must clear after interrupted offline load (2ce2e74b left ApplicationIdle armed permission that could allow a second data: nav)");

                    var before = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    wv.CoreWebView2.NavigateToString("<html><body>INJECTED-OFFLINE-DATA</body></html>");
                    await Task.Delay(800);
                    var html = await wv.CoreWebView2.ExecuteScriptAsync("document.body?.innerText ?? ''");
                    var text = UnquoteWebViewScriptString(html);
                    Assert.DoesNotContain("INJECTED-OFFLINE-DATA", text, StringComparison.Ordinal);
                    var after = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    Assert.Contains("navigation-blocked", after.Replace(before, ""));
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(60));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_browser_process_exit_recreates_operator_panel()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(300);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is not null
                            && (wv.Source?.ToString() ?? "").Contains("/operator/", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    var webViewBeforeKill = wv;
                    var before = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    await ShellBrowserProcessExitHarness.TriggerAsync(wv.CoreWebView2);

                    var recoverDeadline = DateTime.UtcNow.AddSeconds(60);
                    var recovered = false;
                    Microsoft.Web.WebView2.Wpf.WebView2? webViewAfter = null;
                    while (DateTime.UtcNow < recoverDeadline)
                    {
                        await Task.Delay(500);
                        webViewAfter = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (webViewAfter is null || ReferenceEquals(webViewBeforeKill, webViewAfter))
                        {
                            continue;
                        }

                        CoreWebView2? liveCore;
                        try
                        {
                            liveCore = webViewAfter.CoreWebView2;
                        }
                        catch (InvalidOperationException)
                        {
                            continue;
                        }

                        if (liveCore is null)
                        {
                            continue;
                        }

                        var loc = UnquoteWebViewScriptString(
                            await liveCore.ExecuteScriptAsync(
                                "location.pathname + '|' + document.readyState"));
                        if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                            && loc.EndsWith("|complete", StringComparison.Ordinal))
                        {
                            recovered = true;
                            break;
                        }
                    }

                    var after = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    var delta = after.Replace(before, "");
                    Assert.Contains("kind=BrowserProcessExited", delta, StringComparison.Ordinal);
                    Assert.Contains("outcome=recreate-browser", delta, StringComparison.Ordinal);
                    Assert.NotNull(webViewAfter);
                    Assert.NotSame(webViewBeforeKill, webViewAfter);
                    Assert.True(recovered, "Operator panel did not load in recreated WebView2 control");
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(120));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_with_failing_panel_backs_off_and_avoids_offline_flash_loop()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = false;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    var readyDeadline = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < readyDeadline)
                    {
                        await Task.Delay(200);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is not null)
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    ShellFileLog.Write("shell-failing-panel-measure-baseline");
                    var offlineSamples = 0;
                    var panelUnavailableSamples = 0;
                    var runningVisualSamples = 0;
                    var totalSamples = 0;
                    var measureBaselineLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var measureDeadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < measureDeadline)
                    {
                        await Task.Delay(500);
                        totalSamples++;
                        Assert.Equal(ShellServerVisualState.Running, window.PollerVisualStateForTests);
                        runningVisualSamples++;
                        var sample = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify({protocol: location.protocol, href: location.href, offline: document.documentElement.getAttribute('data-shell-offline-page'), title: (document.getElementById('title') || {}).innerText || ''})"));
                        using var doc = JsonDocument.Parse(sample);
                        var href = doc.RootElement.GetProperty("href").GetString() ?? "";
                        var protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "";
                        var offline = doc.RootElement.GetProperty("offline").GetString();
                        var title = doc.RootElement.GetProperty("title").GetString() ?? "";
                        Assert.DoesNotContain("chrome-error://", href, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("edge-error://", href, StringComparison.OrdinalIgnoreCase);
                        if (protocol == "data:" && offline == "1")
                        {
                            offlineSamples++;
                        }

                        if (title.Contains("پنل در دسترس نیست", StringComparison.Ordinal))
                        {
                            panelUnavailableSamples++;
                        }
                    }

                    var measureLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat)
                        .Replace(measureBaselineLog, "");
                    var probeRetries = measureLog.Split('\n')
                        .Count(l => l.Contains("panel-nav-retry", StringComparison.Ordinal)
                            && l.Contains("action=probe", StringComparison.Ordinal));
                    var presentOfflineCount = measureLog.Split('\n')
                        .Count(l => l.Contains("panel-navigate caller=PresentOffline", StringComparison.Ordinal));
                    Assert.True(totalSamples >= 10, "Not enough UI samples during measure window");
                    Assert.Equal(totalSamples, runningVisualSamples);
                    Assert.True(offlineSamples >= (int)(totalSamples * 0.95), $"offline visible in {offlineSamples}/{totalSamples} samples");
                    Assert.True(
                        panelUnavailableSamples >= (int)(totalSamples * 0.95),
                        $"panel-unavailable title in {panelUnavailableSamples}/{totalSamples} samples");
                    Assert.InRange(probeRetries, 3, 5);
                    Assert.InRange(presentOfflineCount, 0, 1);
                    Assert.DoesNotContain("navigation-blocked", measureLog, StringComparison.Ordinal);

                    FakeShellSeams.Http.OperatorPageOk = true;
                    var panelDeadline = DateTime.UtcNow.AddSeconds(75);
                    var panelOk = false;
                    while (DateTime.UtcNow < panelDeadline)
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

                    Assert.True(panelOk, "Operator panel did not load after HTTP probe began succeeding");
                    var finalLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    Assert.Contains("nav-completed-panel-ok", finalLog, StringComparison.Ordinal);
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(150));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_stable_503_two_minutes_has_no_ungated_panel_navigation()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = false;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(90));

                    ShellFileLog.Write("shell-r27e-b1-two-minute-baseline");
                    var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);

                    var measureStart = DateTime.UtcNow;
                    var measureDeadline = measureStart.AddSeconds(125);
                    var samples = new List<PanelOfflineFlashAssert.FlashSample>();
                    while (DateTime.UtcNow < measureDeadline)
                    {
                        await Task.Delay(PanelOfflineFlashAssert.DefaultSampleIntervalMilliseconds);
                        Assert.Equal(ShellServerVisualState.Running, window.PollerVisualStateForTests);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        var elapsedMs = (int)(DateTime.UtcNow - measureStart).TotalMilliseconds;
                        if (wv?.CoreWebView2 is null)
                        {
                            samples.Add(new PanelOfflineFlashAssert.FlashSample(
                                PanelOfflineFlashAssert.SampleViewKind.NoWebView,
                                elapsedMs));
                            continue;
                        }

                        var sample = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify({protocol: location.protocol, offline: document.documentElement.getAttribute('data-shell-offline-page')})"));
                        using var doc = JsonDocument.Parse(sample);
                        var protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "";
                        var offlineAttr = doc.RootElement.GetProperty("offline").GetString();
                        var isOfflineView = protocol == "data:" && offlineAttr == "1";
                        samples.Add(new PanelOfflineFlashAssert.FlashSample(
                            isOfflineView
                                ? PanelOfflineFlashAssert.SampleViewKind.Offline
                                : PanelOfflineFlashAssert.SampleViewKind.NonOffline,
                            elapsedMs));
                    }

                    var measureLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var delta = baseline.Length == 0 ? measureLog : measureLog.Replace(baseline, "");
                    var navigateLines = delta.Split('\n')
                        .Where(l => l.Contains("panel-navigate caller=", StringComparison.Ordinal))
                        .Where(l => !l.Contains("caller=PresentOffline", StringComparison.Ordinal))
                        .ToList();
                    foreach (var navLine in navigateLines)
                    {
                        Assert.Contains("caller=NavigatePanel", navLine, StringComparison.Ordinal);
                    }

                    var probeOkBeforeNav = delta.Split('\n')
                        .Count(l => l.Contains("panel-recovery-probe", StringComparison.Ordinal)
                                    && l.Contains("outcome=ok", StringComparison.Ordinal));
                    Assert.Equal(navigateLines.Count, probeOkBeforeNav);
                    Assert.DoesNotContain("panel-navigate caller=RunningRecover", delta, StringComparison.Ordinal);
                    Assert.Equal(
                        0,
                        delta.Split('\n').Count(l => l.Contains("RunningRecover attempt=", StringComparison.Ordinal)));
                    Assert.Equal(
                        0,
                        delta.Split('\n').Count(l => l.Contains("caller=RunningRecover", StringComparison.Ordinal)));
                    Assert.DoesNotContain(
                        "attempt=exhausted outcome=offline-page",
                        delta,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        "outcome=exhausted-long-retry",
                        delta,
                        StringComparison.Ordinal);
                    Assert.DoesNotContain(
                        "webview-recovery-exhausted",
                        delta,
                        StringComparison.Ordinal);
                    foreach (var sample in samples)
                    {
                        Assert.NotEqual(
                            PanelOfflineFlashAssert.SampleViewKind.NonOffline,
                            sample.ViewKind);
                    }

                    var flash = PanelOfflineFlashAssert.Evaluate(samples);
                    Assert.True(
                        flash.Passes,
                        $"B-1 two-minute 503: {flash.FailureReason} total={flash.TotalSamples} offline={flash.OfflineSamples}");
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(240));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_panel_recovers_exactly_once_when_probe_turns_healthy()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = false;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(60));

                    ShellFileLog.Write("shell-probe-recovery-baseline");
                    var recoveryBaseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);

                    FakeShellSeams.Http.OperatorPageOk = true;
                    var panelDeadline = DateTime.UtcNow.AddSeconds(75);
                    var panelOk = false;
                    while (DateTime.UtcNow < panelDeadline)
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

                    Assert.True(panelOk, "Operator panel did not load after probe turned healthy");
                    var recoveryLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var recoveryDelta = recoveryBaseline.Length == 0
                        ? recoveryLog
                        : recoveryLog.Replace(recoveryBaseline, "");
                    var panelOkCount = recoveryDelta.Split('\n')
                        .Count(l => l.Contains("nav-completed-panel-ok", StringComparison.Ordinal));
                    var presentOfflineCount = recoveryDelta.Split('\n')
                        .Count(l => l.Contains("panel-navigate caller=PresentOffline", StringComparison.Ordinal));
                    Assert.Equal(1, panelOkCount);
                    Assert.InRange(presentOfflineCount, 0, 0);
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(150));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_panel_confirmed_probe_2xx_recovery_after_navigation_failure()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = false;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(60));

                    ShellFileLog.Write("shell-confirmed-2xx-baseline");
                    var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);

                    FakeShellSeams.Http.OperatorPageOk = true;
                    var panelDeadline = DateTime.UtcNow.AddSeconds(75);
                    var panelOk = false;
                    while (DateTime.UtcNow < panelDeadline)
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

                    Assert.True(panelOk, "Operator panel did not load after confirmed probe 2xx recovery");
                    var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var delta = baseline.Length == 0 ? log : log.Replace(baseline, "");
                    Assert.Contains("nav-completed-panel-ok", delta, StringComparison.Ordinal);
                    Assert.Contains("action=probe", delta, StringComparison.Ordinal);
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(150));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_healthy_ticks_do_not_emit_extra_panel_navigations()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = true;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    var panelDeadline = DateTime.UtcNow.AddSeconds(45);
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
                            break;
                        }
                    }

                    ShellFileLog.Write("shell-healthy-tick-baseline");
                    var baseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var navBefore = window.NavigationStartingCount;
                    var measureDeadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < measureDeadline)
                    {
                        await Task.Delay(500);
                    }

                    Assert.Equal(navBefore, window.NavigationStartingCount);
                    var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var delta = baseline.Length == 0 ? log : log.Replace(baseline, "");
                    var navigateCount = delta.Split('\n')
                        .Count(l => l.Contains("panel-navigate caller=", StringComparison.Ordinal));
                    Assert.InRange(navigateCount, 0, 0);
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(120));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_running_panel_transient_probe_ok_then_fail_avoids_offline_flash_loop()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    FakeShellSeams.Install();
                    FakeShellSeams.Http.Answers = true;
                    FakeShellSeams.Http.OperatorPageOk = false;
                    FakeShellSeams.Service.Status = ServiceControllerStatus.Running;

                    window = new MainWindow();
                    window.Show();
                    var wv = await WaitForPanelWebViewAsync(window, TimeSpan.FromSeconds(45));
                    await WaitForPanelUnavailableOfflineAsync(wv, TimeSpan.FromSeconds(60));

                    FakeShellSeams.Http.OperatorPageOk = true;
                    var recoverDeadline = DateTime.UtcNow.AddSeconds(60);
                    var recovered = false;
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
                            recovered = true;
                            break;
                        }
                    }

                    Assert.True(recovered, "Expected one panel recovery before re-failing probe");

                    ShellFileLog.Write("shell-probe-flap-baseline");
                    var flapBaseline = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    FakeShellSeams.Http.OperatorPageOk = false;

                    var flapSamples = new List<PanelOfflineFlashAssert.FlashSample>();
                    var flapWindowStart = DateTime.UtcNow;
                    var flapDeadline = flapWindowStart.AddSeconds(45);
                    while (DateTime.UtcNow < flapDeadline)
                    {
                        await Task.Delay(PanelOfflineFlashAssert.DefaultSampleIntervalMilliseconds);
                        Assert.Equal(ShellServerVisualState.Running, window.PollerVisualStateForTests);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        var elapsedMs = (int)(DateTime.UtcNow - flapWindowStart).TotalMilliseconds;
                        if (wv?.CoreWebView2 is null)
                        {
                            flapSamples.Add(new PanelOfflineFlashAssert.FlashSample(
                                PanelOfflineFlashAssert.SampleViewKind.NoWebView,
                                elapsedMs));
                            continue;
                        }

                        var rawSample = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "JSON.stringify({protocol: location.protocol, offline: document.documentElement.getAttribute('data-shell-offline-page'), path: location.pathname})"));
                        if (string.IsNullOrWhiteSpace(rawSample))
                        {
                            flapSamples.Add(new PanelOfflineFlashAssert.FlashSample(
                                PanelOfflineFlashAssert.SampleViewKind.NoWebView,
                                elapsedMs));
                            continue;
                        }

                        JsonDocument doc;
                        try
                        {
                            doc = JsonDocument.Parse(rawSample);
                        }
                        catch (JsonException)
                        {
                            flapSamples.Add(new PanelOfflineFlashAssert.FlashSample(
                                PanelOfflineFlashAssert.SampleViewKind.NoWebView,
                                elapsedMs));
                            continue;
                        }

                        using (doc)
                        {
                            var protocol = doc.RootElement.GetProperty("protocol").GetString() ?? "";
                            var offlineAttr = doc.RootElement.GetProperty("offline").GetString();
                            var isOfflineView = protocol == "data:" && offlineAttr == "1";
                            var viewKind = isOfflineView
                                ? PanelOfflineFlashAssert.SampleViewKind.Offline
                                : PanelOfflineFlashAssert.SampleViewKind.NonOffline;
                            flapSamples.Add(new PanelOfflineFlashAssert.FlashSample(viewKind, elapsedMs));
                        }
                    }

                    var flapLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    var flapDelta = flapBaseline.Length == 0
                        ? flapLog
                        : flapLog.Replace(flapBaseline, "");
                    var presentOfflineAfterFlap = flapDelta.Split('\n')
                        .Count(l => l.Contains("panel-navigate caller=PresentOffline", StringComparison.Ordinal));
                    var flashMeasure = PanelOfflineFlashAssert.Evaluate(flapSamples);
                    Assert.True(
                        flashMeasure.Passes,
                        $"B-1 anti-flash: {flashMeasure.FailureReason} total={flashMeasure.TotalSamples} offline={flashMeasure.OfflineSamples} offline->panel={flashMeasure.OfflineToPanelTransitions} panel->offline={flashMeasure.PanelToOfflineTransitions}");
                    Assert.InRange(presentOfflineAfterFlap, 0, 1);
                }
                finally
                {
                    FakeShellSeams.RestoreProduction();
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(180));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_renderer_process_crash_reloads_operator_panel()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(300);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (wv?.CoreWebView2 is not null
                            && (wv.Source?.ToString() ?? "").Contains("/operator/", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }

                    Assert.NotNull(wv?.CoreWebView2);
                    Assert.Contains("/operator/", wv.Source?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
                    var before = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    await ShellRendererCrashHarness.TriggerAsync(wv.CoreWebView2);
                    var recoverDeadline = DateTime.UtcNow.AddSeconds(45);
                    var recovered = false;
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
                            recovered = true;
                            break;
                        }
                    }

                    var after = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                    var delta = after.Replace(before, "");
                    Assert.Contains("webview-process-failed", delta);
                    Assert.Contains("kind=RenderProcessExited", delta, StringComparison.Ordinal);
                    Assert.Contains("outcome=reload", delta, StringComparison.Ordinal);
                    Assert.Contains("SHELL-CRASH-TRIGGER", delta, StringComparison.Ordinal);
                    Assert.True(recovered, "Operator panel did not reload after renderer crash");
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(90));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_new_window_blocked_log_redacts_host_in_shell_log()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                const string popupHost = "popup.blocked.test";
                var window = new MainWindow();
                window.Show();
                Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                var webViewDeadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < webViewDeadline)
                {
                    await Task.Delay(300);
                    wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                    if (wv?.CoreWebView2 is not null)
                    {
                        break;
                    }
                }

                Assert.NotNull(wv?.CoreWebView2);
                ShellFileLog.Write("new-window-baseline");
                var before = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                await wv.CoreWebView2.ExecuteScriptAsync($"window.open('https://{popupHost}/secret-path');");
                await Task.Delay(1500);
                var after = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat);
                var delta = after.Replace(before, "");
                Assert.Contains("new-window-blocked", delta);
                Assert.Contains("scheme=https", delta);
                Assert.Contains("host_hash8=", delta);
                Assert.DoesNotContain(popupHost, delta);
                Assert.DoesNotContain("secret-path", delta);
                window.Close();
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
    }

    [Fact]
    public void shell_offline_page_clears_when_service_returns_to_running()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    StopServiceIfRunning();
                    window = new MainWindow();
                    window.Show();
                    Microsoft.Web.WebView2.Wpf.WebView2? wv = null;
                    var offlineSeen = false;
                    var offlineDeadline = DateTime.UtcNow.AddSeconds(45);
                    while (DateTime.UtcNow < offlineDeadline)
                    {
                        await Task.Delay(300);
                        wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        var serviceStatus = ReadCheckpointServiceStatus();
                        Console.WriteLine(
                            $"SHELL-OFFLINE poll UTC={DateTime.UtcNow:O} service={serviceStatus} webViewReady={wv?.CoreWebView2 is not null} uri={wv?.Source}");
                        if (wv?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        var html = await wv.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                        if (UnquoteWebViewScriptString(html).Contains("سرور", StringComparison.Ordinal))
                        {
                            offlineSeen = true;
                            break;
                        }
                    }

                    Assert.True(offlineSeen, "Persian offline page was not shown while service stopped");

                    EnsureServiceRunning();
                    var panelDeadline = DateTime.UtcNow.AddSeconds(45);
                    var panelOk = false;
                    while (DateTime.UtcNow < panelDeadline)
                    {
                        await Task.Delay(500);
                        if (wv?.Source is not null
                            && wv.Source.ToString().Contains("/operator/", StringComparison.OrdinalIgnoreCase))
                        {
                            var html = await wv.CoreWebView2!.ExecuteScriptAsync("document.body.innerText");
                            var text = UnquoteWebViewScriptString(html);
                            if (text.Contains("سرور", StringComparison.Ordinal))
                            {
                                continue;
                            }

                            panelOk = true;
                            break;
                        }
                    }

                    Assert.True(panelOk, "Operator panel did not return after service start");
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(120));
        }
        finally
        {
            CleanupShellDataRoot(root);
            EnsureServiceRunning();
        }
    }

    [Fact]
    public void shell_page_alert_still_shows_webview_default_dialog()
    {
        FakeShellSeams.RestoreProduction();
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow();
                    window.Show();
                    await Task.Delay(4000);
                    var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                    Assert.NotNull(wv?.CoreWebView2);
                    Assert.True(
                        wv.CoreWebView2.Settings.AreDefaultScriptDialogsEnabled,
                        "default script dialogs must stay enabled so WebView2 shows its modal alert");
                    var operatorNav = new TaskCompletionSource<bool>();
                    void OnNavDone(object? _, CoreWebView2NavigationCompletedEventArgs args)
                    {
                        if (args.IsSuccess
                            && (wv.Source?.ToString() ?? "").Contains("/operator/", StringComparison.OrdinalIgnoreCase))
                        {
                            operatorNav.TrySetResult(true);
                        }
                    }

                    wv.CoreWebView2.NavigationCompleted += OnNavDone;
                    var operatorDeadline = DateTime.UtcNow.AddSeconds(20);
                    while (DateTime.UtcNow < operatorDeadline && !operatorNav.Task.IsCompleted)
                    {
                        await Task.Delay(200);
                        var loc = UnquoteWebViewScriptString(
                            await wv.CoreWebView2.ExecuteScriptAsync(
                                "location.pathname + '|' + document.readyState"));
                        if (loc.StartsWith("/operator/", StringComparison.OrdinalIgnoreCase)
                            && loc.EndsWith("|complete", StringComparison.Ordinal))
                        {
                            operatorNav.TrySetResult(true);
                            break;
                        }
                    }

                    Assert.True(operatorNav.Task.IsCompleted, "operator page did not finish navigation");
                    var mainHwnd = new WindowInteropHelper(window).Handle;
                    var webViewHostHwnd = (PresentationSource.FromVisual(wv) as HwndSource)?.Handle ?? IntPtr.Zero;
                    var browserPid = (int)wv.CoreWebView2.BrowserProcessId;
                    var testPid = Process.GetCurrentProcess().Id;

                    var scriptDialogOpeningFired = false;
                    void OnScriptDialogOpening(object? _, CoreWebView2ScriptDialogOpeningEventArgs __)
                    {
                        scriptDialogOpeningFired = true;
                    }

                    wv.CoreWebView2.ScriptDialogOpening += OnScriptDialogOpening;

                    await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                    var dialogEventTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var dialogReceiver = wv.CoreWebView2.GetDevToolsProtocolEventReceiver("Page.javascriptDialogOpening");
                    void OnJavascriptDialogOpening(object? _, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
                    {
                        dialogEventTcs.TrySetResult(args.ParameterObjectAsJson);
                    }

                    dialogReceiver.DevToolsProtocolEventReceived += OnJavascriptDialogOpening;

                    var scriptTask = wv.CoreWebView2.ExecuteScriptAsync("alert('سلام');");
                    var dialogEventArrived = await Task.WhenAny(dialogEventTcs.Task, Task.Delay(5000)) == dialogEventTcs.Task;
                    if (!dialogEventArrived)
                    {
                        PrintShellAlertCdpDiagnostics(mainHwnd, webViewHostHwnd, browserPid, testPid);
                        Assert.Fail(
                            "Page.javascriptDialogOpening DevTools event did not arrive within 5s after alert('سلام');");
                    }

                    var eventJson = await dialogEventTcs.Task;
                    Console.WriteLine($"SHELL-ALERT-CDP event={eventJson}");
                    using (var doc = JsonDocument.Parse(eventJson))
                    {
                        var rootEl = doc.RootElement;
                        Assert.True(
                            rootEl.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "alert",
                            "CDP javascriptDialogOpening type must be alert");
                        Assert.True(
                            rootEl.TryGetProperty("message", out var messageEl)
                            && messageEl.GetString() == "سلام",
                            "CDP javascriptDialogOpening message must be سلام");
                        Assert.True(
                            rootEl.TryGetProperty("hasBrowserHandler", out var hasBrowserHandlerEl)
                            && hasBrowserHandlerEl.ValueKind == JsonValueKind.True,
                            "CDP javascriptDialogOpening hasBrowserHandler must be true");
                    }

                    Assert.False(scriptTask.IsCompleted, "ExecuteScriptAsync must still be blocked while the alert dialog is open");

                    await wv.CoreWebView2.CallDevToolsProtocolMethodAsync(
                        "Page.handleJavaScriptDialog",
                        "{\"accept\":true}");
                    var scriptDone = await Task.WhenAny(scriptTask, Task.Delay(5000)) == scriptTask;
                    Assert.True(
                        scriptDone,
                        "ExecuteScriptAsync did not complete within 5s after Page.handleJavaScriptDialog accept");
                    var scriptResult = await scriptTask;
                    Assert.Equal("null", scriptResult);
                    Assert.False(
                        scriptDialogOpeningFired,
                        "CoreWebView2.ScriptDialogOpening must not fire when observing the dialog via DevTools Protocol only");

                    dialogReceiver.DevToolsProtocolEventReceived -= OnJavascriptDialogOpening;
                    wv.CoreWebView2.ScriptDialogOpening -= OnScriptDialogOpening;
                }
                finally
                {
                    window?.Close();
                }
            }, TimeSpan.FromSeconds(45));
        }
        finally
        {
            CleanupShellDataRoot(root);
        }
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

    private static bool LogLineIsPanelUnavailableNavigated(string line) =>
        line.Contains("branch=present-offline-navigated", StringComparison.Ordinal)
            && line.Contains("offlineState=panel-unavailable", StringComparison.Ordinal);

    private static bool LogTextContainsPanelUnavailableNavigated(string text) =>
        text.Split('\n').Any(LogLineIsPanelUnavailableNavigated);

    private static async Task WaitForPanelUnavailableOfflineAsync(
        Microsoft.Web.WebView2.Wpf.WebView2 wv,
        TimeSpan timeout,
        string? logBaseline = null)
    {
        if (logBaseline is not null)
        {
            if (!LogTextContainsPanelUnavailableNavigated(LogDelta(logBaseline)))
            {
                await AwaitShellLogLineAsync(logBaseline, LogLineIsPanelUnavailableNavigated, timeout);
            }
        }
        else
        {
            var snapshot = ReadShellLogSnapshot();
            if (!LogTextContainsPanelUnavailableNavigated(snapshot))
            {
                await AwaitShellLogLineAsync(snapshot, LogLineIsPanelUnavailableNavigated, timeout);
            }
        }

        await AssertPanelUnavailableOfflineDomAsync(wv, TimeSpan.FromSeconds(10));
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

        using var pollTimer = new System.Threading.Timer(TryMatch, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(50));
        TryMatch(null);
        return await tcs.Task.ConfigureAwait(false);
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
            using var doc = JsonDocument.Parse(sample);
            if (doc.RootElement.GetProperty("protocol").GetString() == "data:"
                && doc.RootElement.GetProperty("offline").GetString() == "1"
                && (doc.RootElement.GetProperty("title").GetString() ?? "")
                    .Contains("پنل در دسترس نیست", StringComparison.Ordinal))
            {
                return;
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

    private static void CleanupShellDataRoot(string root)
    {
        Environment.SetEnvironmentVariable("CHECKPOINT_SHELL_DATA", null);
        try
        {
            var logsDir = Path.Combine(root, "logs");
            if (Directory.Exists(logsDir))
            {
                foreach (var logPath in Directory.GetFiles(logsDir, "shell-*.log"))
                {
                    foreach (var line in File.ReadAllLines(logPath))
                    {
                        if (line.Contains("SHELL-OFFLINE-TRACE", StringComparison.Ordinal)
                            || line.Contains("SHELL-RACE-NAV", StringComparison.Ordinal)
                            || line.Contains("panel-navigate", StringComparison.Ordinal)
                            || line.Contains("SHELL-CRASH-TRIGGER", StringComparison.Ordinal)
                            || line.Contains("webview-process-failed", StringComparison.Ordinal))
                        {
                            Console.WriteLine($"SHELL-TRACE-DUMP root={Path.GetFileName(root)} {line}");
                        }
                    }
                }
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
        catch
        {
        }
    }

    private static string ResolveShellExe()
    {
        var fromEnv = Environment.GetEnvironmentVariable("GAMECLUB_SHELL_EXE");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
        {
            return fromEnv;
        }

        var guessed = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "src", "GameClub.Shell", "bin", "Release", "net48", "GameClub.Shell.exe"));
        if (File.Exists(guessed))
        {
            return guessed;
        }

        throw new FileNotFoundException("GameClub.Shell.exe not found");
    }

    private static Process LaunchShellProcess()
    {
        var root = AllocateShellDataRoot();
        var psi = new ProcessStartInfo(ResolveShellExe())
        {
            UseShellExecute = false,
        };
        psi.EnvironmentVariables["CHECKPOINT_SHELL_DATA"] = root;
        return Process.Start(psi)!;
    }

    private static int ResolveBillingWaitSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("SHELL_A3_BILLING_WAIT_SECONDS");
        if (int.TryParse(raw, out var seconds) && seconds >= 0)
        {
            return seconds;
        }

        return 600;
    }

    private static void EnsureServiceRunning()
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(90));
        }
    }

    private static void StopServiceIfRunning()
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Running)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60));
        }
    }

    private static bool ServiceIsRunning()
    {
        using var sc = new ServiceController(ShellPaths.ServiceName);
        sc.Refresh();
        return sc.Status == ServiceControllerStatus.Running;
    }

    private static object? BackupAutostartValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue(ShellPaths.AutostartValueName);
    }

    private static void ClearAutostartValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue(ShellPaths.AutostartValueName, false);
    }

    private static void RestoreAutostartValue(object? value)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
                        ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (value is null)
        {
            key.DeleteValue(ShellPaths.AutostartValueName, false);
        }
        else
        {
            key.SetValue(ShellPaths.AutostartValueName, value);
        }
    }

    private static void AssertFrozenBillSegmentsMatch(ShellCiHttp.FrozenBillDetail refBill, ShellCiHttp.FrozenBillDetail shellBill)
    {
        Assert.Equal(refBill.Segments.Count, shellBill.Segments.Count);
        for (var i = 0; i < refBill.Segments.Count; i++)
        {
            var r = refBill.Segments[i];
            var s = shellBill.Segments[i];
            Assert.Equal(r.Mode, s.Mode);
            Assert.Equal(r.PlayerCount, s.PlayerCount);
            Assert.Equal(r.HourlyToman, s.HourlyToman);
        }
    }

    private static void WriteGeometryTimeline(string test, string phase, Window window)
    {
        Console.WriteLine(
            $"SHELL-GEOMETRY test={test} phase={phase} UTC={DateTime.UtcNow:O} "
            + $"state={window.WindowState} left={window.Left:0} top={window.Top:0} width={window.Width:0} height={window.Height:0}");
    }

    private static void WriteShellGeom(string test, string phase, Window window)
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        Console.WriteLine(
            $"SHELL-GEOM test={test} phase={phase} UTC={DateTime.UtcNow:O} "
            + $"state={window.WindowState} left={window.Left:0} top={window.Top:0} width={window.Width:0} height={window.Height:0} "
            + $"minW={window.MinWidth:0} minH={window.MinHeight:0} sizeToContent={window.SizeToContent} "
            + $"workAreaW={area.Width} workAreaH={area.Height}");
    }

    private static string ReadCheckpointServiceStatus()
    {
        try
        {
            using var sc = new ServiceController(ShellPaths.ServiceName);
            sc.Refresh();
            return sc.Status.ToString();
        }
        catch (Exception ex)
        {
            return "error:" + ex.GetType().Name;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static HashSet<int> CollectWebViewRelatedProcessIds(int browserProcessId, int testProcessId)
    {
        var ids = new HashSet<int> { browserProcessId, testProcessId };
        foreach (var proc in Process.GetProcessesByName("msedgewebview2"))
        {
            try
            {
                ids.Add(proc.Id);
            }
            catch
            {
                // process may exit between enum and Id read
            }
            finally
            {
                proc.Dispose();
            }
        }

        return ids;
    }

    private static string ReadWindowClass(IntPtr hWnd)
    {
        var classSb = new StringBuilder(256);
        GetClassName(hWnd, classSb, classSb.Capacity);
        return classSb.ToString();
    }

    private static void PrintShellAlertCdpDiagnostics(
        IntPtr mainWindowHwnd,
        IntPtr webViewHostHwnd,
        int browserProcessId,
        int testProcessId)
    {
        void LogChildTree(IntPtr parent, string scope)
        {
            if (parent == IntPtr.Zero)
            {
                Console.WriteLine($"SHELL-ALERT-DIAG {scope} parent=(null) — skipping EnumChildWindows");
                return;
            }

            EnumChildWindows(
                parent,
                (hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out var pidU);
                    var className = ReadWindowClass(hWnd);
                    var visible = IsWindowVisible(hWnd);
                    Console.WriteLine(
                        $"SHELL-ALERT-DIAG {scope} hwnd=0x{hWnd.ToInt64():X} class={className} pid={pidU} visible={visible}");
                    LogChildTree(hWnd, scope + "/child");
                    return true;
                },
                IntPtr.Zero);
        }

        LogChildTree(mainWindowHwnd, "main-child");
        LogChildTree(webViewHostHwnd, "webview-host-child");

        var allowedPids = CollectWebViewRelatedProcessIds(browserProcessId, testProcessId);
        const string ChromeWidgetPrefix = "Chrome_WidgetWin_";

        void LogChromeWidgetIfMatch(IntPtr hWnd, string kind)
        {
            GetWindowThreadProcessId(hWnd, out var pidU);
            var pid = (int)pidU;
            if (!allowedPids.Contains(pid))
            {
                return;
            }

            var className = ReadWindowClass(hWnd);
            if (!className.StartsWith(ChromeWidgetPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var visible = IsWindowVisible(hWnd);
            Console.WriteLine(
                $"SHELL-ALERT-DIAG chrome-{kind} hwnd=0x{hWnd.ToInt64():X} class={className} pid={pid} visible={visible}");
        }

        void EnumSubtree(IntPtr hWnd, string kind)
        {
            LogChromeWidgetIfMatch(hWnd, kind);
            EnumChildWindows(
                hWnd,
                (child, _) =>
                {
                    EnumSubtree(child, "descendant");
                    return true;
                },
                IntPtr.Zero);
        }

        EnumWindows(
            (hWnd, _) =>
            {
                EnumSubtree(hWnd, "toplevel");
                return true;
            },
            IntPtr.Zero);
    }

    private static string UnquoteWebViewScriptString(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || string.Equals(json, "null", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }
        catch
        {
            return json.Trim('"');
        }
    }
}
#endif