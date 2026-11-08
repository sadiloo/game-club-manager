using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace GameClub.Kiosk.Core.Connection;

public static class KioskConnectionFactory
{
    public static HubConnection Create(
        Uri hubUri,
        string deviceId,
        string deviceKey,
        bool legacyWindows)
    {
        var transport = legacyWindows
            ? HttpTransportType.LongPolling
            : HttpTransportType.LongPolling | HttpTransportType.ServerSentEvents;
        return new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.Transports = transport;
                options.Headers["X-Device-Id"] = deviceId;
                options.Headers["X-Device-Key"] = deviceKey;
            })
            .WithAutomaticReconnect()
            .Build();
    }
}
