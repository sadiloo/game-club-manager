#if NET48
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GameClub.Kiosk.Core.Runtime;
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Wpf;

internal static class WpfLiveServerTransportHelper
{
    public static async Task WaitForKioskRuntimeTransportConnectedAsync(object kioskHost, TimeSpan timeout)
    {
        var runtime = kioskHost.GetType().GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(kioskHost) as KioskRuntime;
        Assert.NotNull(runtime);
        await runtime.WaitForTransportConnectedAsync(timeout, CancellationToken.None);
    }
}
#endif