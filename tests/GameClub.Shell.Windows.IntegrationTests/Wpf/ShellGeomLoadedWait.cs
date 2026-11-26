#if NET48
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Xunit.Sdk;

namespace GameClub.Shell.Windows.IntegrationTests.Wpf;

internal static class ShellGeomLoadedWait
{
    public static void RunLoadedAsserts(Window window, string testName, Action assertions)
    {
        try
        {
            assertions();
        }
        catch (Exception ex)
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            var rb = window.RestoreBounds;
            var detail =
                $"{testName} Loaded assert failed: {ex.Message}. "
                + $"RestoreBounds=({rb.Left:0},{rb.Top:0},{rb.Width:0},{rb.Height:0}) "
                + $"Width={window.Width:0} Height={window.Height:0} "
                + $"ActualWidth={window.ActualWidth:0} ActualHeight={window.ActualHeight:0} "
                + $"MinWidth={window.MinWidth:0} MinHeight={window.MinHeight:0} "
                + $"WindowState={window.WindowState} workAreaW={area.Width} workAreaH={area.Height}";
            throw new XunitException(detail);
        }
    }

    public static void AwaitLoaded(TaskCompletionSource<bool> loadedGate, string testName, TimeSpan timeout)
    {
        if (!loadedGate.Task.Wait(timeout))
        {
            throw new TimeoutException($"{testName} Loaded assertions did not run");
        }

        loadedGate.Task.GetAwaiter().GetResult();
    }
}
#endif