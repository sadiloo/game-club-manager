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
                    ("http://127.0.0.1:5000.evil.com/operator/", "127.0.0.1"),
                    ("http://127.0.0.1:5000@evil.com/operator/", "evil.com"),
                    ("https://127.0.0.1:5000/operator/", "127.0.0.1"),
                };
                foreach (var (uri, expectedHost) in spoofed)
                {
                    var beforeAttempt = logCursor;
                    wv.CoreWebView2.Navigate(uri);
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
                            break;
                        }
                    }

                    Assert.False(
                        string.IsNullOrEmpty(blockedLine),
                        $"Expected navigation-blocked log with host_hash8={hash8} for {uri}");
                    await Task.Delay(300);
                    var href = UnquoteWebViewScriptString(await wv.CoreWebView2.ExecuteScriptAsync("location.href"));
                    Assert.DoesNotContain("evil.com", href, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(":50001", href, StringComparison.Ordinal);
                    Assert.False(
                        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
                        $"WebView committed blocked URI {uri}");
                }

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
                    var browserPid = (int)wv.CoreWebView2.BrowserProcessId;
                    using (var browserProc = Process.GetProcessById(browserPid))
                    {
                        browserProc.Kill();
                        browserProc.WaitForExit(15_000);
                    }

                    var recoverDeadline = DateTime.UtcNow.AddSeconds(60);
                    var recovered = false;
                    Microsoft.Web.WebView2.Wpf.WebView2? webViewAfter = null;
                    while (DateTime.UtcNow < recoverDeadline)
                    {
                        await Task.Delay(500);
                        webViewAfter = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                        if (webViewAfter?.CoreWebView2 is null)
                        {
                            continue;
                        }

                        if (ReferenceEquals(webViewBeforeKill, webViewAfter))
                        {
                            continue;
                        }

                        var loc = UnquoteWebViewScriptString(
                            await webViewAfter.CoreWebView2.ExecuteScriptAsync(
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
                    ShellSeams.PanelNavigationUrlOverride = "http://127.0.0.1:9/operator/";
                    FakeShellSeams.Http.Answers = true;
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
                    var dataNavStarts = 0;
                    var panelNavigateCount = 0;
                    var logPath = "";
                    void OnNavStarting(object? _, CoreWebView2NavigationStartingEventArgs args)
                    {
                        if (args.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            dataNavStarts++;
                        }
                    }

                    wv.CoreWebView2.NavigationStarting += OnNavStarting;
                    var measureStart = DateTime.UtcNow;
                    var measureDeadline = measureStart.AddSeconds(60);
                    while (DateTime.UtcNow < measureDeadline)
                    {
                        await Task.Delay(500);
                        var log = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                            .Select(File.ReadAllText)
                            .Aggregate("", string.Concat);
                        logPath = log;
                        panelNavigateCount = log.Split('\n')
                            .Count(l => l.Contains("panel-navigate caller=NavigatePanel", StringComparison.Ordinal));
                    }

                    wv.CoreWebView2.NavigationStarting -= OnNavStarting;
                    Assert.InRange(panelNavigateCount, 1, 5);
                    Assert.InRange(dataNavStarts, 1, 1);

                    ShellSeams.PanelNavigationUrlOverride = null;
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

                    Assert.True(panelOk, "Operator panel did not load after clearing failing panel URL override");
                    var finalLog = Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log")
                        .Select(File.ReadAllText)
                        .Aggregate("", string.Concat);
                    Assert.Contains("nav-completed-panel-ok", finalLog, StringComparison.Ordinal);
                    var postSuccessNavigates = finalLog.Split('\n')
                        .Count(l => l.Contains("panel-navigate caller=NavigatePanel", StringComparison.Ordinal));
                    Assert.InRange(postSuccessNavigates, 1, 6);
                }
                finally
                {
                    ShellSeams.PanelNavigationUrlOverride = null;
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
                    await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                    await wv.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
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
                            || line.Contains("panel-navigate", StringComparison.Ordinal))
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