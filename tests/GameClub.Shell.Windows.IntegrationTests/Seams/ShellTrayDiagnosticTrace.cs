#if NET48
using System;
using GameClub.Shell.Shell;

namespace GameClub.Shell.Windows.IntegrationTests.Seams;

internal static class ShellTrayDiagnosticTrace
{
    private static bool _wired;

    public static void Wire()
    {
        if (_wired)
        {
            return;
        }

        _wired = true;
        ShellSeams.TrayStopInProgressTrace = (value, caller, stack) =>
        {
            var shortStack = ShortStack(stack);
            Console.WriteLine(
                $"SHELL-TRAY-TRACE TrayStopInProgress={value} caller={caller} stack={shortStack}");
        };

        ShellSeams.OnPollerTickTrace = pollerId =>
        {
            Console.WriteLine($"SHELL-TRAY-TRACE pollerTick pollerId={pollerId}");
        };
    }

    private static string ShortStack(string stack)
    {
        var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var take = Math.Min(4, lines.Length);
        return string.Join(" | ", lines, 0, take);
    }
}
#endif