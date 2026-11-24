#if NET48
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using GameClub.Shell.Shell;
using Microsoft.Web.WebView2.Core;

namespace GameClub.Shell.Windows.IntegrationTests.Shell;

internal static class ShellRendererCrashHarness
{
    public static async Task TriggerAsync(CoreWebView2 core)
    {
        var rendererFailed = new TaskCompletionSource<CoreWebView2ProcessFailedKind>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnProcessFailed(object? _, CoreWebView2ProcessFailedEventArgs e)
        {
            if (IsRendererProcessFailure(e.ProcessFailedKind))
            {
                rendererFailed.TrySetResult(e.ProcessFailedKind);
            }
        }

        core.ProcessFailed += OnProcessFailed;
        try
        {
            var devtoolsWereEnabled = core.Settings.AreDevToolsEnabled;
            core.Settings.AreDevToolsEnabled = true;
            try
            {
                await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Page.crash", "{}");
                }
                catch (Exception)
                {
                    if (!await AwaitRendererFailureAsync(rendererFailed, TimeSpan.FromSeconds(3)))
                    {
                        await KillRendererForCoreAsync(core);
                        WriteCrashTrigger("kill-renderer");
                        await AwaitRendererFailureOrThrowAsync(
                            rendererFailed,
                            TimeSpan.FromSeconds(5),
                            "RenderProcessExited did not arrive within 5s after kill-renderer (Page.crash exception path).");
                        return;
                    }
                }

                if (!await AwaitRendererFailureAsync(rendererFailed, TimeSpan.FromSeconds(3)))
                {
                    await KillRendererForCoreAsync(core);
                    WriteCrashTrigger("kill-renderer");
                    await AwaitRendererFailureOrThrowAsync(
                        rendererFailed,
                        TimeSpan.FromSeconds(5),
                        "RenderProcessExited did not arrive within 5s after kill-renderer (no renderer failure after Page.crash).");
                    return;
                }

                WriteCrashTrigger("cdp");
            }
            finally
            {
                core.Settings.AreDevToolsEnabled = devtoolsWereEnabled;
            }
        }
        finally
        {
            core.ProcessFailed -= OnProcessFailed;
        }
    }

    private static void WriteCrashTrigger(string path)
    {
        var line = $"SHELL-CRASH-TRIGGER path={path}";
        ShellFileLog.Write(line);
        Console.WriteLine(line);
    }

    private static bool IsRendererProcessFailure(CoreWebView2ProcessFailedKind kind) =>
        kind == CoreWebView2ProcessFailedKind.RenderProcessExited;

    private static async Task<bool> AwaitRendererFailureAsync(
        TaskCompletionSource<CoreWebView2ProcessFailedKind> tcs,
        TimeSpan timeout)
    {
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }

    private static async Task AwaitRendererFailureOrThrowAsync(
        TaskCompletionSource<CoreWebView2ProcessFailedKind> tcs,
        TimeSpan timeout,
        string timeoutMessage)
    {
        if (!await AwaitRendererFailureAsync(tcs, timeout))
        {
            throw new InvalidOperationException(timeoutMessage);
        }
    }

    private static async Task KillRendererForCoreAsync(CoreWebView2 core)
    {
        var environment = core.Environment
            ?? throw new InvalidOperationException("CoreWebView2.Environment is null");

        var extended = await environment.GetProcessExtendedInfosAsync();
        var panelSource = core.Source ?? "";
        var candidates = extended
            .Where(p => p.ProcessInfo.Kind == CoreWebView2ProcessKind.Renderer)
            .ToList();

        CoreWebView2ProcessExtendedInfo? match = null;
        foreach (var candidate in candidates)
        {
            if (candidate.AssociatedFrameInfos.Any(f =>
                    string.Equals(f.Source, panelSource, StringComparison.OrdinalIgnoreCase)
                    || (f.Source ?? "").Contains("/operator/", StringComparison.OrdinalIgnoreCase)))
            {
                match = candidate;
                break;
            }
        }

        match ??= candidates.FirstOrDefault(c => c.AssociatedFrameInfos.Count > 0);
        if (match is null)
        {
            throw new InvalidOperationException(
                "No renderer process with associated frames found for panel WebView2");
        }

        var pid = match.ProcessInfo.ProcessId;
        using var proc = Process.GetProcessById(pid);
        proc.Kill();
        proc.WaitForExit(15_000);
    }
}
#endif