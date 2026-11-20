# PR #42 shell R5 report (staging)

Base: `beccaf4867bd77c2518d5c28a4ad0d5afe4d307b` on `cursor/sub-round-a-shell-f022`. Pushed to `cursor/sub-round-a-shell-staging` only.

## Compile gate

Release with `-p:EnableWindowsTargeting=true` on `GameClub.Server`, `GameClub.Shell`, `GameClub.Shell.Windows.IntegrationTests`, `GameClub.Server.Tests` — **0 errors**.

## SRC

| Change | File:line | Fixes (CI) |
|--------|-----------|------------|
| **S1** Pending offline + failed loopback `NavigationCompleted` → Persian `NavigateToString` | `MainWindow.xaml.cs` (pending fields, `PresentOfflinePageAsync`, `OnNavigationCompleted`) | `shell_shows_persian_offline_when_service_stopped`, `shell_shows_persian_offline_after_service_stops_post_webview_ready` |
| **S2** Blocked navigation log `scheme=` + `host_hash8=` (no raw URL) | `MainWindow.xaml.cs:82-96` | `shell_blocks_external_navigation_and_new_window_with_log_line` |
| **S3** Restore bounds (Left/Top/Width/Height) in **Normal**, then **Maximized** in **SourceInitialized** only | `WindowPlacementStore.cs:28-49` | `shell_window_json_restores_size_position_maximized`, `shell_window_json_infinity_input_round_trips…`, `shell_window_json_corrupt_file_falls_back…` |

**S3 rationale:** `SourceInitialized` is deterministic (HWND exists, before first `Show()`). Apply normalized restore bounds while `WindowState.Normal`, then set `Maximized` so WPF does not leave `Normal` when size is applied before show.

### Full diff — `MainWindow.xaml.cs`

```diff
diff --git a/src/GameClub.Shell/MainWindow.xaml.cs b/src/GameClub.Shell/MainWindow.xaml.cs
index 1c0de94..94a79af 100644
--- a/src/GameClub.Shell/MainWindow.xaml.cs
+++ b/src/GameClub.Shell/MainWindow.xaml.cs
@@ -1,6 +1,8 @@
 using System;
 using System.IO;
 using System.Reflection;
+using System.Security.Cryptography;
+using System.Text;
 using System.Threading.Tasks;
 using System.Windows;
 using System.Windows.Input;
@@ -18,6 +20,9 @@ public partial class MainWindow : Window
     private int _navigationStartingCount;
     private bool _webViewReady;
     private DateTime _lastReconnectAttempt = DateTime.MinValue;
+    private bool _pendingOffline;
+    private string _pendingOfflineState = "stopped";
+    private string? _lastNavigationUri;
 
     public int NavigationStartingCount => _navigationStartingCount;
     public double CurrentZoomFactor => PanelWebView?.ZoomFactor ?? 1.0;
@@ -75,11 +80,21 @@ public partial class MainWindow : Window
 
             PanelWebView.CoreWebView2.NavigationStarting += (_, args) =>
             {
+                _lastNavigationUri = args.Uri;
                 _navigationStartingCount++;
                 if (!args.Uri.StartsWith(ShellPaths.PanelOrigin, StringComparison.OrdinalIgnoreCase))
                 {
                     args.Cancel = true;
-                    ShellFileLog.Write($"navigation-blocked url={args.Uri}");
+                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var blocked))
+                    {
+                        var host = (blocked.Host ?? "").ToLowerInvariant();
+                        var hash8 = Sha256Hex8(host);
+                        ShellFileLog.Write($"navigation-blocked scheme={blocked.Scheme} host_hash8={hash8}");
+                    }
+                    else
+                    {
+                        ShellFileLog.Write("navigation-blocked scheme=unknown host_hash8=00000000");
+                    }
                 }
             };
 
@@ -89,10 +104,21 @@ public partial class MainWindow : Window
                 ShellFileLog.Write($"new-window-blocked url={args.Uri}");
             };
 
+            PanelWebView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
+
             _webViewReady = true;
             PanelWebView.Visibility = Visibility.Visible;
             NativeHost.Visibility = Visibility.Collapsed;
-            await NavigatePanelAsync();
+
+            if (_pendingOffline)
+            {
+                _pendingOffline = false;
+                await PresentOfflinePageAsync(_pendingOfflineState);
+            }
+            else
+            {
+                await NavigatePanelAsync();
+            }
         }
         catch
         {
@@ -103,6 +129,45 @@ public partial class MainWindow : Window
         }
     }
 
+    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
+    {
+        if (e.IsSuccess || !IsLoopbackPanelTarget(_lastNavigationUri ?? ""))
+        {
+            return;
+        }
+
+        var state = _poller?.Current.VisualState switch
+        {
+            ShellServerVisualState.Starting => "starting",
+            _ => "stopped",
+        };
+        _ = PresentOfflinePageAsync(state);
+    }
+
+    private static bool IsLoopbackPanelTarget(string uriString)
+    {
+        if (!Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
+        {
+            return false;
+        }
+
+        var host = (uri.Host ?? "").ToLowerInvariant();
+        return host is "127.0.0.1" or "localhost" or "::1";
+    }
+
+    private static string Sha256Hex8(string lowercaseHost)
+    {
+        using var sha = SHA256.Create();
+        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(lowercaseHost));
+        var hex = new StringBuilder(bytes.Length * 2);
+        foreach (var b in bytes)
+        {
+            hex.Append(b.ToString("x2"));
+        }
+
+        return hex.Length >= 8 ? hex.ToString(0, 8) : hex.ToString().PadRight(8, '0');
+    }
+
     private void OnShellPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
     {
         if (!_webViewReady || !PanelWebView.IsFocused)
@@ -164,8 +229,16 @@ public partial class MainWindow : Window
 
     private async Task ShowOfflineAsync(ServerStateSnapshot snap)
     {
+        var state = snap.VisualState switch
+        {
+            ShellServerVisualState.Starting => "starting",
+            _ => "stopped",
+        };
+
         if (!_webViewReady)
         {
+            _pendingOffline = true;
+            _pendingOfflineState = state;
             return;
         }
 
@@ -175,13 +248,7 @@ public partial class MainWindow : Window
         }
 
         _lastReconnectAttempt = DateTime.UtcNow;
-        var state = snap.VisualState switch
-        {
-            ShellServerVisualState.Starting => "starting",
-            _ => "stopped",
-        };
-        var html = LoadOfflineHtml(state);
-        PanelWebView.NavigateToString(html);
+        await PresentOfflinePageAsync(state);
         await Task.Delay(5000);
         if (_poller?.Current.OperatorAnswers == true)
         {
@@ -189,6 +256,20 @@ public partial class MainWindow : Window
         }
     }
 
+    private Task PresentOfflinePageAsync(string state)
+    {
+        if (!_webViewReady)
+        {
+            _pendingOffline = true;
+            _pendingOfflineState = state;
+            return Task.CompletedTask;
+        }
+
+        var html = LoadOfflineHtml(state);
+        PanelWebView.NavigateToString(html);
+        return Task.CompletedTask;
+    }
+
     private static string LoadOfflineHtml(string state)
     {
         using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GameClub.Shell.Assets.offline.html")
```

### Full diff — `WindowPlacementStore.cs`

```diff
diff --git a/src/GameClub.Shell/Shell/WindowPlacementStore.cs b/src/GameClub.Shell/Shell/WindowPlacementStore.cs
index ffd9f10..1f2a003 100644
--- a/src/GameClub.Shell/Shell/WindowPlacementStore.cs
+++ b/src/GameClub.Shell/Shell/WindowPlacementStore.cs
@@ -27,27 +27,20 @@ internal static class WindowPlacementStore
 
         void ApplyNow()
         {
+            var (left, top) = ClampToWorkingArea(placement.Left, placement.Top, placement.Width, placement.Height);
+            window.WindowState = WindowState.Normal;
+            window.Left = left;
+            window.Top = top;
             window.Width = placement.Width;
             window.Height = placement.Height;
             if (placement.IsMaximized)
             {
                 window.WindowState = WindowState.Maximized;
             }
-            else
-            {
-                window.WindowState = WindowState.Normal;
-                var (left, top) = ClampToWorkingArea(placement.Left, placement.Top, placement.Width, placement.Height);
-                window.Left = left;
-                window.Top = top;
-            }
-        }
-
-        if (window.IsLoaded)
-        {
-            ApplyNow();
-            return;
         }
 
+        // SourceInitialized runs once the HWND exists and before the first Show(); applying
+        // restore bounds then WindowState here avoids WPF resetting Maximized when bounds are set earlier.
         window.SourceInitialized += OnSourceInitialized;
 
         void OnSourceInitialized(object? sender, EventArgs e)
```

## Harness (no assertion edits)

| ID | File:line | Fixes |
|----|-----------|-------|
| **H1** | `ShellPlanAIntegrationTests.cs:105` — `window.Show()` before placement asserts | `shell_window_json_restores_size_position_maximized` |
| **H2** | `WpfStaApplicationFixture.cs:42-88` — reset harness statics, close all windows on start/timeout/failure | leaked windows; poll/toast count bleed |
| **H3** | `FakeShellSeams.cs` — `ResetHarnessStatics()` per `RunAsync` | `shell_unexpected_service_stop…` (7→1), reentrancy toasts |
| **H4** | `ShellPollerReentrancyTests.cs:63` — `BuildSnapshotForTests()` before `UtcNow +91` | `shell_no_answer_to_stopped_shows_exactly_one_toast` |
| **H5a** | `ShellCiHttp.cs:37-40` — POST `bill/open` once per session, then GET only | `shell_events_do_not_change_open_session_amount_after_ten_minutes` (409 at :205) |
| **H5e** | `ShellPlanAIntegrationTests.cs:505-507` — fire `ExecuteScriptAsync(alert)` without awaiting; `WhenAny` on dialog TCS | `shell_page_alert_still_shows_webview_default_dialog` (45 s timeout) |

Also: `finally { window?.Close(); }` on offline + page_alert tests (H2).

## Not done

Windows shell test re-run on this Linux VM (net48 runtime tests are CI-only).

## Open questions

None.