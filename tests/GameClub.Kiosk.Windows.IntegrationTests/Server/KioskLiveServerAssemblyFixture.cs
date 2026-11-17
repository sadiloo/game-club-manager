#if NET10_0_OR_GREATER
namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

/// <summary>Fails assembly teardown when a test-started live server is still running.</summary>
public sealed class KioskLiveServerAssemblyFixture : IDisposable
{
    public void Dispose() => KioskLiveServerRegistry.AssertNoTrackedServersAlive();
}
#endif