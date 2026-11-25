#if NET10_0_OR_GREATER
using Xunit;

namespace GameClub.Kiosk.Windows.IntegrationTests.Server;

public sealed class OutOfProcessLiveServerPortTests
{
    [Fact]
    public void reserved_loopback_http_port_does_not_use_postgres_service_ports()
    {
        for (var i = 0; i < 32; i++)
        {
            var port = OutOfProcessLiveServer.ReserveLoopbackHttpPort();
            Assert.NotEqual(5432, port);
            Assert.NotEqual(5433, port);
        }
    }
}
#endif