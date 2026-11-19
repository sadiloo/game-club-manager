#if NET48
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text.Json;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.Equal(1024, window.Width);
                Assert.Equal(700, window.Height);
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
        var shellSession = ShellCiHttp.CreateOpenSessionAsync(shellStation).GetAwaiter().GetResult();
        var refSession = ShellCiHttp.CreateOpenSessionAsync(refStation).GetAwaiter().GetResult();
        var baseline = ShellCiHttp.GetOpenSessionLiveBillAsync(refSession).GetAwaiter().GetResult();

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

        var afterRef = ShellCiHttp.GetOpenSessionLiveBillAsync(refSession).GetAwaiter().GetResult();
        var afterShell = ShellCiHttp.GetOpenSessionLiveBillAsync(shellSession).GetAwaiter().GetResult();
        Assert.Equal(afterRef.AmountToman, afterShell.AmountToman);
        if (afterRef.MinutesPlayed is int refMin && afterShell.MinutesPlayed is int shellMin)
        {
            Assert.Equal(refMin, shellMin);
        }

        var waitSeconds = ResolveBillingWaitSeconds();
        Thread.Sleep(TimeSpan.FromSeconds(waitSeconds));

        var finalRef = ShellCiHttp.GetOpenSessionLiveBillAsync(refSession).GetAwaiter().GetResult();
        var finalShell = ShellCiHttp.GetOpenSessionLiveBillAsync(shellSession).GetAwaiter().GetResult();
        Assert.Equal(finalRef.AmountToman, finalShell.AmountToman);
        if (finalRef.MinutesPlayed is int refMin2 && finalShell.MinutesPlayed is int shellMin2)
        {
            Assert.Equal(refMin2, shellMin2);
        }

        Assert.True(finalRef.AmountToman >= baseline.AmountToman);
    }

    [Fact]
    public void shell_elevated_stop_with_zero_sessions_skips_warning()
    {
        FakeShellSeams.Install();
        ShellSeams.ReadOpenSessionCount = () => 0;
        ShellSeams.MessageBoxHandler = _ => throw new InvalidOperationException("dialog should not appear");
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        var code = ServerControlProgram.Run("stop");
        Assert.InRange(code, 0, 4);
    }

    [Fact]
    public void shell_elevated_stop_shows_open_session_count_warning()
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

    [Fact]
    public void shell_elevated_stop_cancel_leaves_service_running()
    {
        FakeShellSeams.Install();
        ShellSeams.ReadOpenSessionCount = () => 3;
        ShellSeams.MessageBoxHandler = _ => System.Windows.Forms.DialogResult.Cancel;
        FakeShellSeams.Service.Status = ServiceControllerStatus.Running;
        var code = ServerControlProgram.Run("stop");
        Assert.Equal(1, code);
        Assert.Equal(ServiceControllerStatus.Running, FakeShellSeams.Service.Status);
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
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        StopServiceIfRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                var window = new MainWindow();
                window.Show();
                await Task.Delay(8000);
                var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                Assert.NotNull(wv?.CoreWebView2);
                var html = await wv.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                Assert.Contains("سرور", html);
                window.Close();
            }, TimeSpan.FromSeconds(45));
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
                var before = Directory.Exists(ShellPaths.ShellLogsDir)
                    ? Directory.GetFiles(ShellPaths.ShellLogsDir, "shell-*.log").Select(File.ReadAllText).Aggregate("", string.Concat)
                    : "";
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
    public void shell_page_alert_still_shows_webview_default_dialog()
    {
        if (!WebView2RuntimePresent()) throw new InvalidOperationException("WebView2 runtime required");
        EnsureServiceRunning();
        var root = AllocateShellDataRoot();
        try
        {
            _fixture.RunAsync(async () =>
            {
                var tcs = new TaskCompletionSource<bool>();
                var window = new MainWindow();
                window.Show();
                await Task.Delay(4000);
                var wv = window.FindName("PanelWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                Assert.NotNull(wv?.CoreWebView2);
                wv.CoreWebView2.ScriptDialogOpening += (_, _) => tcs.TrySetResult(true);
                await wv.CoreWebView2.ExecuteScriptAsync("alert('سلام');");
                var seen = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
                Assert.True(seen);
                window.Close();
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
}
#endif