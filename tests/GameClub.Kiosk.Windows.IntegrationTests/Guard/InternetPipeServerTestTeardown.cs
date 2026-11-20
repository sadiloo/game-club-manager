#if NET48
#nullable disable
using System;
using System.IO.Pipes;
using System.Threading;
using GameClub.Kiosk.Core.Guard;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Guard;

internal static class InternetPipeServerTestTeardown
{
    internal static void Stop(CancellationTokenSource cts, Thread pipeThread, TimeSpan joinBudget)
    {
        cts?.Cancel();
        TryUnblock();
        if (pipeThread is not null)
        {
            Assert.True(pipeThread.Join(joinBudget), "InternetPipeServer thread did not stop");
        }
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