#if NET48
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using Microsoft.Web.WebView2.Core;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

internal static class ShellBrowserProcessExitHarness
{
    public static async Task TriggerAsync(CoreWebView2 core)
    {
        var browserExited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProcessFailed(object? _, CoreWebView2ProcessFailedEventArgs e)
        {
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                browserExited.TrySetResult(true);
            }
        }

        CoreWebView2Environment? environment = null;
        EventHandler<CoreWebView2BrowserProcessExitedEventArgs>? onEnvironmentExit = null;
        core.ProcessFailed += OnProcessFailed;
        try
        {
            environment = core.Environment
                ?? throw new InvalidOperationException("CoreWebView2.Environment is null");
            onEnvironmentExit = (_, _) => browserExited.TrySetResult(true);
            environment.BrowserProcessExited += onEnvironmentExit;

            var pid = (int)core.BrowserProcessId;
            using var proc = Process.GetProcessById(pid);
            proc.Kill();
            proc.WaitForExit(15_000);
            ShellFileLog.Write($"SHELL-BROWSER-EXIT-TRIGGER pid={pid}");
            Console.WriteLine($"SHELL-BROWSER-EXIT-TRIGGER pid={pid}");

            if (!await AwaitBrowserExitAsync(browserExited, TimeSpan.FromSeconds(15)))
            {
                throw new InvalidOperationException(
                    "BrowserProcessExited did not arrive within 15s after killing browser process.");
            }
        }
        finally
        {
            core.ProcessFailed -= OnProcessFailed;
            if (environment is not null && onEnvironmentExit is not null)
            {
                environment.BrowserProcessExited -= onEnvironmentExit;
            }
        }
    }

    private static async Task<bool> AwaitBrowserExitAsync(TaskCompletionSource<bool> tcs, TimeSpan timeout)
    {
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }
}
#endif