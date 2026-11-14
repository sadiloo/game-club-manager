using GameClub.Kiosk.Core.Connection;
using GameClub.Kiosk.Core.Runtime;

namespace GameClub.Kiosk.Core.Tests;

public sealed class KioskR2CoreTests
{
    [Fact]
    public void kiosk_connection_factory_builds_hub_for_device_headers()
    {
        var connection = KioskConnectionFactory.Create(new Uri("http://127.0.0.1/hubs/kiosk"), "device-id", "device-key", legacyWindows: true);
        Assert.NotNull(connection);
    }

    [Fact]
    public void kiosk_runtime_exposes_offline_queue_after_configure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kiosk-rt-{Guid.NewGuid():N}.db");
        var runtime = new KioskRuntime(() => new HttpClient());
        runtime.Configure(new Uri("http://127.0.0.1/hubs/kiosk"), Guid.NewGuid(), "k", "PC-1", path, legacyWindows: true);
        Assert.NotNull(runtime.GetQueue());
    }
}
