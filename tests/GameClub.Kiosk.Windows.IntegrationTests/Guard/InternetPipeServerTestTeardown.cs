#if NET48
#nullable disable
using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using GameClub.Kiosk.Core.Guard;
using GameClub.Kiosk.Guard.Internet;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class InternetPipeServerTestTeardown
{
    internal static void Stop(CancellationTokenSource cts, Thread pipeThread, TimeSpan? joinBudget = null)
    {
        var budget = joinBudget ?? InternetPipeServerShutdownBudgets.PipeThreadTeardownBudget;
        var origin = Stopwatch.GetTimestamp();
        void Timeline(string evt)
        {
            var ms = (Stopwatch.GetTimestamp() - origin) * 1000 / Stopwatch.Frequency;
            Console.WriteLine($"PIPE_TIMELINE ms={ms} event=teardown_{evt}");
        }

        Timeline("stop_begin");
        cts?.Cancel();
        TryUnblock();
        Timeline("stop_unblock_done");
        if (pipeThread is not null)
        {
            Timeline($"join_begin budget_ms={(int)budget.TotalMilliseconds}");
            Assert.True(pipeThread.Join(budget), "InternetPipeServer thread did not stop");
            Timeline("join_end");
        }

        Timeline("stop_end");
    }

    internal static void TryUnblock()
    {
        try
        {
            using var unblock = new NamedPipeClientStream(".", GuardInternetClient.PipeName, PipeDirection.InOut);
            unblock.Connect(1_000);
        }
        catch (Exception)
        {
            /* listener already stopped or between accept iterations */
        }
    }
}
#endif